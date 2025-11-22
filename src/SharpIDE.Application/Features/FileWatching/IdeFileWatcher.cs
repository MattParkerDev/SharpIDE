using FileWatcherEx;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public sealed class IdeFileWatcher(ILogger<IdeFileWatcher> logger) : IDisposable
{
	private Matcher? _matcher;
	private FileSystemWatcherEx? _fileWatcher;
	private SharpIdeSolutionModel? _solution;

	private readonly ILogger<IdeFileWatcher> _logger = logger;

	public void StartWatching(SharpIdeSolutionModel solution)
	{
		_solution = solution;

		var matcher = new Matcher();
		//matcher.AddIncludePatterns(["**/*.cs", "**/*.csproj", "**/*.sln"]);
		matcher.AddIncludePatterns(["**/*"]);
		matcher.AddExcludePatterns(["**/bin", "**/obj", "**/node_modules", "**/.vs", "**/.git", "**/.idea", "**/.vscode", "**/*.g.cs"]);
		_matcher = matcher;

		var fileWatcher = new FileSystemWatcherEx();
		fileWatcher.FolderPath = solution.DirectoryPath;
		//fileWatcher.Filters.AddRange(["*"]);
		fileWatcher.IncludeSubdirectories = true;
		fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
		fileWatcher.OnChanged += OnEvent;
		fileWatcher.OnCreated += OnEvent;
		fileWatcher.OnDeleted += OnEvent;
		fileWatcher.OnRenamed += OnEvent;
		fileWatcher.OnError += (s, e) => _logger.LogError(e.GetException(), "FileSystemWatcher: Error");

		fileWatcher.Start();
		_fileWatcher = fileWatcher;
	}

	public void StopWatching()
	{
		if (_fileWatcher is not null)
		{
			_fileWatcher.Stop();
			_fileWatcher.Dispose();
			_fileWatcher = null!;
		}
	}

	// TODO: Put events on a queue and process them in the background to avoid filling the buffer? FileSystemWatcherEx might already handle this
	private void OnEvent(object? sender, FileChangedEvent e)
	{
		var matchResult = _matcher!.Match(_solution!.DirectoryPath, e.FullPath);
		if (!matchResult.HasMatches) return;
		switch (e.ChangeType)
		{
			case ChangeType.CHANGED: HandleChanged(e.FullPath); break;
			case ChangeType.CREATED: HandleCreated(e.FullPath); break;
			case ChangeType.DELETED: HandleDeleted(e.FullPath); break;
			case ChangeType.RENAMED: HandleRenamed(e.OldFullPath!, e.FullPath); break;
			default: throw new ArgumentOutOfRangeException();
		}
	}

	private void HandleRenamed(string oldFullPath, string fullPath)
	{
		var isDirectory = Path.HasExtension(fullPath) is false;
		if (isDirectory)
		{
			GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryRenamed.InvokeParallelFireAndForget(oldFullPath, fullPath);
		}
		else
		{
			GlobalEvents.Instance.FileSystemWatcherInternal.FileRenamed.InvokeParallelFireAndForget(oldFullPath, fullPath);
		}
		_logger.LogTrace("FileSystemWatcher: Renamed - {OldFullPath} -> {FullPath}", oldFullPath, fullPath);
	}

	private void HandleDeleted(string fullPath)
	{
		var isDirectory = Path.HasExtension(fullPath) is false;
		if (isDirectory)
		{
			GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryDeleted.InvokeParallelFireAndForget(fullPath);
		}
		else
		{
			GlobalEvents.Instance.FileSystemWatcherInternal.FileDeleted.InvokeParallelFireAndForget(fullPath);
		}
		_logger.LogTrace("FileSystemWatcher: Deleted - {FullPath}", fullPath);
	}

	private void HandleCreated(string fullPath)
	{
		var isDirectory = Path.HasExtension(fullPath) is false;
		if (isDirectory)
		{
			GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryCreated.InvokeParallelFireAndForget(fullPath);
		}
		else
		{
			GlobalEvents.Instance.FileSystemWatcherInternal.FileCreated.InvokeParallelFireAndForget(fullPath);
		}
		_logger.LogTrace("FileSystemWatcher: Created - {FullPath}", fullPath);
	}

	// The only changed event we care about is files, not directories
	// We will naively assume that if the file name does not have an extension, it's a directory
	// This may not always be true, but it lets us avoid reading the file system to check
	// TODO: Make a note to users that they should not use files without extensions
	private void HandleChanged(string fullPath)
	{
		if (Path.HasExtension(fullPath) is false) return; // we don't care about directory changes
		_logger.LogTrace("FileSystemWatcher: Changed - {FullPath}", fullPath);
		GlobalEvents.Instance.FileSystemWatcherInternal.FileChanged.InvokeParallelFireAndForget(fullPath);
	}

	public void Dispose()
	{
		StopWatching();
	}
}
