using System.IO;

namespace MessageVault {

	/// <summary>
	/// Abstracts page interactions. This way we can put all the complex code in <see cref="SegmentWriter"/>
	/// and swap <see cref="IPageWriter"/> implementations to write to file disk or memory (for unit tests).
	/// </summary>
	public interface IPageWriter {
		void Init();
		
		void EnsureSize(long size);
		byte[] ReadPage(long offset);
		void Save(Stream stream, long offset);
	}

}