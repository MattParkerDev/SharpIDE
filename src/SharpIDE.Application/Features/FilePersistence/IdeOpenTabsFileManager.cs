using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FilePersistence;
#pragma warning disable VSTHRD011

/// Holds the in memory copies of files, and manages saving/loading them to/from disk.
public class IdeOpenTabsFileManager(ILogger<IdeOpenTabsFileManager> logger)
{
	private readonly ILogger<IdeOpenTabsFileManager> _logger = logger;

	private sealed class OpenFileEntry
	{
		public required Lazy<Task<string>> TextTask { get; set; }
		public int ReferenceCount { get; set; }
	}

	private ConcurrentDictionary<SharpIdeFile, OpenFileEntry> _openFiles = new();

	public void TrackOpenFile(SharpIdeFile file)
	{
		_openFiles.AddOrUpdate(
			file,
			_ => new OpenFileEntry
			{
				TextTask = new Lazy<Task<string>>(Task<string> () => File.ReadAllTextAsync(file.Path)),
				ReferenceCount = 1
			},
			(_, existing) =>
			{
				existing.ReferenceCount++;
				return existing;
			});
	}

	/// Implicitly 'opens' a file if not already open, and returns the text.
	public async Task<string> GetFileTextAsync(SharpIdeFile file)
	{
		var entry = _openFiles.GetOrAdd(file, f =>
		{
			return new OpenFileEntry
			{
				TextTask = new Lazy<Task<string>>(Task<string> () => File.ReadAllTextAsync(f.Path)),
				ReferenceCount = 0
			};
		});
		var textTask = entry.TextTask.Value;
		var text = await textTask;
		return text;
	}

	public async Task<string?> GetFileTextIfOpenAsync(string filePath)
	{
		var normalizedPath = Path.GetFullPath(filePath);
		var openFile = _openFiles.Keys.FirstOrDefault(file =>
			string.Equals(Path.GetFullPath(file.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
		if (openFile is null)
		{
			return null;
		}

		return await GetFileTextAsync(openFile);
	}

	// Calling this assumes that the file is already open - may need to be revisited for code fixes and refactorings. I think all files involved in a multi-file fix/refactor shall just be saved to disk immediately.
	public async Task UpdateFileTextInMemory(SharpIdeFile file, string newText)
	{
		if (!_openFiles.ContainsKey(file)) throw new InvalidOperationException("File is not open in memory.");

		var entry = _openFiles[file];
		entry.TextTask = new Lazy<Task<string>>(() => Task.FromResult(newText));
	}

	public async Task UpdateFileTextInMemoryIfOpen(SharpIdeFile file, string newText)
	{
		if (!_openFiles.ContainsKey(file)) return;

		var entry = _openFiles[file];
		entry.TextTask = new Lazy<Task<string>>(() => Task.FromResult(newText));
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

	private async Task WriteAllText(SharpIdeFile file, string text)
	{
		file.SuppressDiskChangeEvents = true;
		await File.WriteAllTextAsync(file.Path, text);
		file.LastIdeWriteTime = DateTimeOffset.Now;
		file.SuppressDiskChangeEvents = false;
		_logger.LogInformation("IdeOpenTabsFileManager: Saved file {FilePath}", file.Path);
	}

	public async Task SaveAllOpenFilesAsync()
	{
		foreach (var file in _openFiles.Keys.ToList())
		{
			await SaveFileAsync(file);
		}
	}

	public void CloseFile(SharpIdeFile file)
	{
		if (!_openFiles.TryGetValue(file, out var entry)) return;
		if (entry.ReferenceCount <= 1)
		{
			_openFiles.TryRemove(file, out _);
			return;
		}

		entry.ReferenceCount--;
	}
}

#pragma warning restore VSTHRD011
