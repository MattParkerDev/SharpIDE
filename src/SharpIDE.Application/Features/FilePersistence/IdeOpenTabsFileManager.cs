using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FilePersistence;
#pragma warning disable VSTHRD011

/// Holds the in memory copies of open files, and manages saving/loading them to/from disk.
/// An open file's text is owned by its editor, which reports every change via FileChangedService.OpenFileTextChanged.
public class IdeOpenTabsFileManager(IdeFileSystemLock fileSystemLock, ILogger<IdeOpenTabsFileManager> logger)
{
	private readonly IdeFileSystemLock _fileSystemLock = fileSystemLock;
	private readonly ILogger<IdeOpenTabsFileManager> _logger = logger;

	private readonly ConcurrentDictionary<SharpIdeFile, Lazy<Task<SourceText>>> _openFiles = new();

	/// Implicitly 'opens' a file if not already open, and returns the text.
	public async Task<SourceText> GetFileTextAsync(SharpIdeFile file)
	{
		var textTaskLazy = _openFiles.GetOrAdd(file, f => new Lazy<Task<SourceText>>(async () => SourceText.From(await File.ReadAllTextAsync(f.Path))));
		return await textTaskLazy.Value;
	}

	public bool IsOpen(SharpIdeFile file) => _openFiles.ContainsKey(file);

	/// The current (editor) text of each open file that has loaded
	public IReadOnlyList<(SharpIdeFile File, SourceText Text)> GetOpenFileTexts() => _openFiles
		.Where(s => s.Value.IsValueCreated && s.Value.Value.IsCompletedSuccessfully)
		.Select(s => (s.Key, s.Value.Value.Result))
		.ToList();

	/// Returns false if the file is not open, e.g. a late text change from an editor that is closing
	public bool TryUpdateFileTextInMemory(SharpIdeFile file, SourceText newText)
	{
		if (_openFiles.TryGetValue(file, out var existing) is false) return false;
		var textTask = Task.FromResult(newText);
		return _openFiles.TryUpdate(file, new Lazy<Task<SourceText>>(() => textTask), existing);
	}

	public async Task SaveFileAsync(SharpIdeFile file)
	{
		if (!_openFiles.ContainsKey(file)) throw new InvalidOperationException("File is not open in memory.");
		if (file.IsDirty.Value is false) return;

		var text = await GetFileTextAsync(file);
		await WriteAllText(file, text);
		file.IsDirty.Value = false;
	}

	/// For files that are not open, e.g. those changed by a code action or rename
	public async Task WriteClosedFileAsync(SharpIdeFile file, SourceText text)
	{
		if (_openFiles.ContainsKey(file)) throw new InvalidOperationException("File is open - its editor owns its text.");
		await WriteAllText(file, text);
	}

	/// Under the file system lock, recording the stamp of what we wrote - so the watcher recognises this write as ours, and a reconcile never sees it half done
	private async Task WriteAllText(SharpIdeFile file, SourceText text)
	{
		using var _ = await _fileSystemLock.Lock.LockAsync();
		await File.WriteAllTextAsync(file.Path, text.ToString());
		file.DiskStamp = FileStamp.TryRead(file.Path);
		_logger.LogInformation("IdeOpenTabsFileManager: Saved file {FilePath}", file.Path);
	}

	public async Task SaveAllOpenFilesAsync()
	{
		foreach (var file in _openFiles.Keys.ToList())
		{
			await SaveFileAsync(file);
		}
	}

	/// Closes the file synchronously (before the first await), then saves it if it had unsaved changes, so Roslyn and disk agree again.
	public async Task CloseFileAsync(SharpIdeFile file, bool saveIfDirty)
	{
		if (_openFiles.TryRemove(file, out var textTaskLazy) is false) return;
		if (saveIfDirty is false || file.IsDirty.Value is false) return;
		var text = await textTaskLazy.Value;
		await WriteAllText(file, text);
		file.IsDirty.Value = false;
	}
}

#pragma warning restore VSTHRD011
