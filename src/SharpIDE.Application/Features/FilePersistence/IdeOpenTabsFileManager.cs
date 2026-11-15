using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FilePersistence;

/// Holds the in memory copies of open files, and manages saving/loading them to/from disk.
/// An open file's text is owned by its editor, which reports every change via FileChangedService.OpenFileTextChanged.
public class IdeOpenTabsFileManager(IdeFileSystemLock fileSystemLock, ILogger<IdeOpenTabsFileManager> logger)
{
	private readonly IdeFileSystemLock _fileSystemLock = fileSystemLock;
	private readonly ILogger<IdeOpenTabsFileManager> _logger = logger;

	/// Each open file's current text. Replaced (never mutated) on every change, so a reference comparison tells whether the text has changed.
	private readonly ConcurrentDictionary<SharpIdeFile, Task<SourceText>> _openFiles = new();

	/// Implicitly 'opens' a file if not already open, and returns the text.
	/// If the read fails, the file is left closed, so a later open retries it.
	public async Task<SourceText> GetFileTextAsync(SharpIdeFile file)
	{
		if (_openFiles.TryGetValue(file, out var existing)) return await existing;

		var loadTcs = new TaskCompletionSource<SourceText>(TaskCreationOptions.RunContinuationsAsynchronously);
		var textTask = _openFiles.GetOrAdd(file, loadTcs.Task);
		if (textTask != loadTcs.Task) return await textTask; // Another caller is opening it

		try
		{
			var text = SourceText.From(await File.ReadAllTextAsync(file.Path));
			loadTcs.SetResult(text);
			return text;
		}
		catch (Exception e)
		{
			_openFiles.TryRemove(KeyValuePair.Create(file, loadTcs.Task));
			loadTcs.SetException(e); // Observed by any concurrent caller
			throw;
		}
	}

	public bool IsOpen(SharpIdeFile file) => _openFiles.ContainsKey(file);

	/// The current (editor) text of each open file that has loaded
	public IReadOnlyList<(SharpIdeFile File, SourceText Text)> GetOpenFileTexts() => _openFiles
		.Where(s => s.Value.IsCompletedSuccessfully)
#pragma warning disable VSTHRD002 // Only completed tasks - this never blocks
		.Select(s => (s.Key, s.Value.Result))
#pragma warning restore VSTHRD002
		.ToList();

	/// Returns false if the file is not open, e.g. a late text change from an editor that is closing
	public bool TryUpdateFileTextInMemory(SharpIdeFile file, SourceText newText)
	{
		var textTask = Task.FromResult(newText);
		while (_openFiles.TryGetValue(file, out var existing))
		{
			if (_openFiles.TryUpdate(file, textTask, existing)) return true;
		}
		return false;
	}

	public async Task SaveFileAsync(SharpIdeFile file)
	{
		if (_openFiles.TryGetValue(file, out var textTask) is false) throw new InvalidOperationException("File is not open in memory.");
		await SaveIfDirty(file, textTask);
	}

	/// Saves every open file with unsaved changes. Files closed part way through are skipped (closing saves them), and one failure doesn't stop the rest.
	public async Task SaveAllOpenFilesAsync()
	{
		foreach (var (file, textTask) in _openFiles.ToList())
		{
			try
			{
				await SaveIfDirty(file, textTask);
			}
			catch (Exception e)
			{
				_logger.LogError(e, "IdeOpenTabsFileManager: Failed to save file {FilePath}", file.Path);
			}
		}
	}

	private async Task SaveIfDirty(SharpIdeFile file, Task<SourceText> textTask)
	{
		if (file.IsDirty.Value is false) return;
		var text = await textTask;
		await WriteAllText(file, text);
		file.IsDirty.Value = false;
		// An edit made while we were writing isn't on disk. Checked after clearing the flag, as an edit replaces the text before marking the file dirty -
		// so either we see the new text here, or the edit's dirty flag lands after ours.
		if (_openFiles.TryGetValue(file, out var current) && current != textTask) file.IsDirty.Value = true;
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

	/// Closes the file synchronously (before the first await), then saves it if it had unsaved changes, so Roslyn and disk agree again.
	public async Task CloseFileAsync(SharpIdeFile file, bool saveIfDirty)
	{
		if (_openFiles.TryRemove(file, out var textTask) is false) return;
		if (saveIfDirty is false || file.IsDirty.Value is false) return;
		var text = await textTask;
		await WriteAllText(file, text);
		file.IsDirty.Value = false;
	}
}
