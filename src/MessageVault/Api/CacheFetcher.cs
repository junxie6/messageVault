using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessageVault.Cloud;
using MessageVault.Files;
using MessageVault.MemoryPool;

namespace MessageVault.Api {

	/// <summary>
	/// Fetches multiple caches in parallel
	/// </summary>
	public sealed class CacheManager {
		readonly DirectoryInfo _cacheFolder;
		readonly IMemoryStreamManager _manager;
		readonly CacheFetcher[] _fetchers;

		public CacheManager(IDictionary<string, string> nameAndSas, DirectoryInfo cacheFolder,
			IMemoryStreamManager manager) {
			_cacheFolder = cacheFolder;
			_manager = manager;

			_fetchers = nameAndSas
				.Select(p =>
				{
					var fetcher = new CacheFetcher(p.Value, p.Key, _cacheFolder, _manager);
					fetcher.Init();
					return fetcher;
				})
				.ToArray();
		}

		public IList<CacheFetcher> GetFetchers() {
			return _fetchers;
		} 

		public void Run(CancellationToken token) {

			var array = new Task<FetchResult>[_fetchers.Length];

			
			
			while (!token.IsCancellationRequested) {
				
				for (int i = 0; i < _fetchers.Length; i++) {
					array[i] = _fetchers[i].DownloadNext();
				}
				
				Task.WaitAll(array, token);
				var downloaded = array.Sum(t => t.Result.DownloadedBytes);
				if (downloaded == 0) {
					// no activity
					token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
				}
			}
		}
	}

	public delegate void MessageHandler(MessageWithId id, long currentPosition, long maxPosition);

	public sealed class MessageHandlerClosure {
		public  MessageWithId Message;
		public long CurrentCachePosition;
		public long MaxCachePosition;
	}

	public sealed class ReadResult {
		public int ReadRecords;
		public long CurrentCachePosition;
		public long StartingCachePosition;
		public long AvailableCachePosition;
		public bool ReadEndOfCacheBeforeItWasFlushed;
	}

	public sealed class ReadBulkResult {
		public int ReadRecords;
		public long CurrentCachePosition;
		public long StartingCachePosition;
		public long AvailableCachePosition;
		public bool ReadEndOfCacheBeforeItWasFlushed;
		public IList<MessageHandlerClosure> Messages;

	}


	

	public sealed class FetchResult {
		public long CurrentRemotePosition;
		public long MaxRemotePosition;
		public long CurrentCachePosition;
		public long DownloadedBytes;
		public long DownloadedRecords;
		public long UsedBytes;
		public long SavedBytes;
	}

	public sealed class CacheFetcher {
		readonly CloudPageReader _remote;
		readonly CloudCheckpointReader _remotePos;

		public const string CachePositionName = "cache-v3.pos";
		public const string CacheStreamName = "cache-v3.dat";


		readonly FileStream _cacheWriter;
		
		readonly FileCheckpointArrayWriter _cacheChk;

		readonly IMemoryStreamManager _streamManager;
		readonly BinaryWriter _writer;
		readonly static Encoding CacheFormat = new UTF8Encoding(false);
		public readonly string StreamName;
		

		public static CacheFetcher CreateStandalone(string sas, string stream, DirectoryInfo folder) {
			var fetcher = new CacheFetcher(sas, stream, folder, new MemoryStreamFactoryManager());
			fetcher.Init();
			return fetcher;
		}

		public CacheReader CreateReaderInstance() {
			return ReaderInstance(_outputFile, _outputCheckpoint, _cacheChk);
		}

		public static CacheReader ReaderInstance(FileInfo dataFile, FileInfo checkpointFile, FileCheckpointArrayWriter fileCheckpointArrayWriter) {
			var cacheReader = dataFile.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var checkpointReader = new FileCheckpointArrayReader(checkpointFile, 2);
			return new CacheReader(fileCheckpointArrayWriter, cacheReader, checkpointReader);
		}

		public CacheFetcher(string sas, string stream, DirectoryInfo folder, IMemoryStreamManager streamManager) {
			StreamName = stream;
			_streamManager = streamManager;
			var raw = CloudSetup.GetReaderRaw(sas);

			_remote = raw.Item2;
			_remotePos = raw.Item1;

			var streamDir = Path.Combine(folder.FullName, stream);
			var di = new DirectoryInfo(streamDir);
			if (!di.Exists)
			{
				di.Create();
			}
			_outputFile = new FileInfo(Path.Combine(di.FullName, CacheStreamName));
			_outputCheckpoint = new FileInfo(Path.Combine(di.FullName, CachePositionName));

			_outputFile.Refresh();
			_cacheWriter = _outputFile.Open(_outputFile.Exists ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
			
			_writer = new BinaryWriter(_cacheWriter,CacheFormat);
			_cacheChk = new FileCheckpointArrayWriter(_outputCheckpoint, 2);
		}

		static bool TryRead(BinaryReader reader, out MessageWithId msg) {
			try {
				msg = StorageFormat.Read(reader);
				return true;
			}
			catch (EndOfStreamException) {
				msg = null;
				return false;
			}
		}

		public int AmountToLoadMax = 10*1024*1024;
		FileInfo _outputFile;
		FileInfo _outputCheckpoint;

		public void Init() {
			_cacheChk.GetOrInitPosition();
		}

		
	

		public async Task<FetchResult> DownloadNext() {
			var pos = _cacheChk.ReadPositionVolatile();

			var convertedLocalPos = pos[0];
			var currentRemotePosition = pos[1];
			

			var maxRemotePos = _remotePos.Read();
			var result = new FetchResult() {
				MaxRemotePosition = maxRemotePos,
				CurrentRemotePosition = currentRemotePosition,
				CurrentCachePosition = convertedLocalPos,
				DownloadedBytes = 0,
				UsedBytes = 0,
				SavedBytes = 0
			};

			if (maxRemotePos <= currentRemotePosition) {
				// we don't have anything to write
				return result;
			}

			var availableAmount = maxRemotePos - currentRemotePosition;
			var amountToLoad = Math.Min(availableAmount, AmountToLoadMax);
			
			long usedBytes = 0;
			long downloadedRecords = 0;

			using (var mem = _streamManager.GetStream("fetcher")) {
				await _remote.DownloadRangeToStreamAsync(mem, currentRemotePosition, (int) amountToLoad)
					.ConfigureAwait(false);
				// cool, we've got some data back

				result.DownloadedBytes = mem.Position;
				mem.Seek(0, SeekOrigin.Begin);


				_cacheWriter.Seek(convertedLocalPos, SeekOrigin.Begin);

				var pages = new List<MessageWithId>();
				using (var bin = new BinaryReader(mem)) {
					MessageWithId msg;
					while (TryRead(bin, out msg)) {
						var hasMorePagesToRead = HasMore(msg);

						if (false == hasMorePagesToRead && pages.Count == 0) {
							// fast path, we can save message directly without merging
							WriteMessage(msg);
							downloadedRecords += 1;
							usedBytes = mem.Position;
							continue;
						}

						pages.Add(msg);
						if (hasMorePagesToRead){
							continue;
						}
						
						var total = pages.Sum(m => m.Value.Length);
						using (var sub = _streamManager.GetStream("chase-1", total))
						{
							foreach (var page in pages)
							{
								sub.Write(page.Value, 0, page.Value.Length);
							}
							sub.Seek(0, SeekOrigin.Begin);
							var last = pages.Last();
							var final = new MessageWithId(last.Id, last.Attributes, last.Key, sub.ToArray(), 0);
							WriteMessage(final);
							usedBytes = mem.Position;
							downloadedRecords +=1;
						}
						pages.Clear();

					}
					
				}
				if (usedBytes == 0) {
					return result;
				}
				_writer.Flush();
				_cacheWriter.Flush(true);
				_cacheChk.Update(new[] {
					_cacheWriter.Position,
					currentRemotePosition + usedBytes,
				});

				result.UsedBytes = usedBytes;
				result.SavedBytes = _cacheWriter.Position - result.CurrentCachePosition;
				result.DownloadedRecords = downloadedRecords;
				return result;
			}
		}

		

		void WriteMessage(MessageWithId msg) {
			EnsureSizeToAvoidFragmentation();
			CacheStorage.Write(_writer, msg);
		}

		void EnsureSizeToAvoidFragmentation()
		{
			const long shouldHaveMb = 2*1024*1024;
			const long increaseByMb = 5*shouldHaveMb;
			var current = _cacheWriter.Length;
			var pos = _cacheWriter.Position;

			var available = current - pos;
			if (available < shouldHaveMb) {
				_cacheWriter.SetLength(_cacheWriter.Length + increaseByMb);
			}
		}

		static bool HasMore(MessageWithId msg) {
			var hasMore = ((MessageFlags) msg.Attributes & MessageFlags.ToBeContinued) ==
			              MessageFlags.ToBeContinued;
			return hasMore;
		}
	}

}