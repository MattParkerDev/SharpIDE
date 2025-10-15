using System.Collections.Concurrent;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FilePersistence;
#pragma warning disable VSTHRD011

/// Holds the in memory copies of files, and manages saving/loading them to/from disk.
public class IdeFileManager
{
	private ConcurrentDictionary<SharpIdeFile, Lazy<Task<string>>> _openFiles = new();

	/// Implicitly 'opens' a file if not already open, and returns the text.
	public async Task<string> GetFileTextAsync(SharpIdeFile file)
	{
		var textTaskLazy = _openFiles.GetOrAdd(file, f =>
		{
			var lazy = new Lazy<Task<string>>(Task<string> () => File.ReadAllTextAsync(f.Path));
			return lazy;
		});
		var textTask = textTaskLazy.Value;
		var text = await textTask;
		return text;
	}

	// Calling this assumes that the file is already open - may need to be revisited for code fixes and refactorings. I think all files involved in a multi-file fix/refactor shall just be saved to disk immediately.
	public async Task UpdateFileTextInMemory(SharpIdeFile file, string newText)
	{
		if (!_openFiles.ContainsKey(file)) throw new InvalidOperationException("File is not open in memory.");

		var newLazyTask = new Lazy<Task<string>>(() => Task.FromResult(newText));
		_openFiles[file] = newLazyTask;
		// Potentially should be event based?
		if (file.IsRoslynWorkspaceFile)
		{
			await RoslynAnalysis.UpdateDocument(file, newText);
		}
	}

	public async Task ReloadFileFromDisk(SharpIdeFile file)
	{
		if (!_openFiles.ContainsKey(file)) throw new InvalidOperationException("File is not open in memory.");

		var newTextTaskLazy = new Lazy<Task<string>>(() => File.ReadAllTextAsync(file.Path));
		_openFiles[file] = newTextTaskLazy;
		var textTask = newTextTaskLazy.Value;
		if (file.IsRoslynWorkspaceFile)
		{
			var text = await textTask;
			await RoslynAnalysis.UpdateDocument(file, text);
		}
	}

	public async Task SaveFileAsync(SharpIdeFile file)
	{
		if (!_openFiles.ContainsKey(file)) throw new InvalidOperationException("File is not open in memory.");
		if (file.IsDirty.Value is false) return;

		var text = await GetFileTextAsync(file);
		await WriteAllText(file, text);
		file.IsDirty.Value = false;
	}

	public async Task UpdateInMemoryIfOpenAndSaveAsync(SharpIdeFile file, string newText)
	{
		if (_openFiles.ContainsKey(file))
		{
			await UpdateFileTextInMemory(file, newText);
			await SaveFileAsync(file);
		}
		else
		{
			await WriteAllText(file, newText);
		}
	}

	private static async Task WriteAllText(SharpIdeFile file, string text)
	{
		file.SuppressDiskChangeEvents = true;
		await File.WriteAllTextAsync(file.Path, text);
		Console.WriteLine($"Saved file {file.Path}");
		file.LastIdeWriteTime = DateTimeOffset.Now;
		file.SuppressDiskChangeEvents = false;
	}

	public async Task SaveAllOpenFilesAsync()
	{
		foreach (var file in _openFiles.Keys.ToList())
		{
			await SaveFileAsync(file);
		}
	}
}

#pragma warning restore VSTHRD011
