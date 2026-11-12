using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileWatching;

/// Watches the solution folder, and passes each event on as a hint ("look at this path") - IdeFileExternalChangeHandler works out what actually changed.
/// So the oddities of OS file watching (merged, reordered, split or lost events) don't matter. A lost event (buffer overflow) means a rescan.
/// Handlers only filter and enqueue, to drain the OS buffer as fast as possible.
public sealed class IdeFileWatcher(IdeFileExternalChangeHandler changeHandler, ILogger<IdeFileWatcher> logger) : IDisposable
{
	private readonly IdeFileExternalChangeHandler _changeHandler = changeHandler;
	private readonly ILogger<IdeFileWatcher> _logger = logger;
	private FileSystemWatcher? _watcher;
	private string _rootPath = null!;

	public void StartWatching(SharpIdeSolutionModel solution)
	{
		StopWatching();
		_rootPath = solution.RootFolder.Path;
		var watcher = new FileSystemWatcher(_rootPath)
		{
			IncludeSubdirectories = true,
			// The max recommended. Builds write a lot to bin/obj, which we can only filter out after the OS has buffered the events.
			InternalBufferSize = 64 * 1024,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
		};
		watcher.Changed += OnEvent;
		watcher.Created += OnEvent;
		watcher.Deleted += OnEvent;
		watcher.Renamed += OnRenamed;
		watcher.Error += OnError;
		watcher.EnableRaisingEvents = true;
		_watcher = watcher;
		// Anything that changed between the solution being read and now would otherwise be missed
		_changeHandler.RescanAll();
	}

	public void StopWatching()
	{
		_watcher?.Dispose();
		_watcher = null;
	}

	private void OnEvent(object sender, FileSystemEventArgs e)
	{
		if (TreeMapperV2.IsInExcludedFolder(_rootPath, e.FullPath)) return;
		_changeHandler.PathChanged(e.FullPath);
	}

	private void OnRenamed(object sender, RenamedEventArgs e)
	{
		var isOldPathExcluded = TreeMapperV2.IsInExcludedFolder(_rootPath, e.OldFullPath);
		var isNewPathExcluded = TreeMapperV2.IsInExcludedFolder(_rootPath, e.FullPath);
		if (isOldPathExcluded is false && isNewPathExcluded is false) _changeHandler.PathRenamed(e.OldFullPath, e.FullPath);
		else if (isOldPathExcluded is false) _changeHandler.PathChanged(e.OldFullPath); // Moved into an excluded folder - gone, as far as we're concerned
		else if (isNewPathExcluded is false) _changeHandler.PathChanged(e.FullPath);
	}

	private void OnError(object sender, ErrorEventArgs e)
	{
		// Typically the buffer overflowed, so events were lost
		_logger.LogWarning(e.GetException(), "IdeFileWatcher: Error, rescanning the solution folder");
		_changeHandler.RescanAll();
	}

	public void Dispose() => StopWatching();
}
