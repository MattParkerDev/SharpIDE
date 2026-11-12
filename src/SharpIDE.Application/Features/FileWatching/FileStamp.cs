using NeoSmart.AsyncLock;

namespace SharpIDE.Application.Features.FileWatching;

/// A cheap identity for a file's contents on disk, like VS Code's etag and IntelliJ's VFS timestamp + length.
/// If a file's stamp still matches the one we recorded when we last read or wrote it, it hasn't changed since - so our own writes are recognised without any timing window.
public readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length)
{
	public static FileStamp From(FileInfo fileInfo) => new(fileInfo.LastWriteTimeUtc, fileInfo.Length);

	public static FileStamp? TryRead(string filePath)
	{
		var fileInfo = new FileInfo(filePath);
		return fileInfo.Exists ? From(fileInfo) : null;
	}
}

/// Serialises everything that changes the file system tree, or writes files, with reconciling the disk against the model.
/// So a reconcile never sees a write we haven't recorded the stamp of yet (or a half-written file of ours).
public class IdeFileSystemLock
{
	public AsyncLock Lock { get; } = new();
}
