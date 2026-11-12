using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileWatching;

/// Watches each of the solution's root folders, and passes each event on as a hint ("look at this path") - IdeFileExternalChangeHandler works out what actually changed.
/// So the oddities of OS file watching (merged, reordered, split or lost events) don't matter. A lost event (buffer overflow) means a rescan.
/// Handlers only filter and enqueue, to drain the OS buffer as fast as possible.
public sealed class IdeFileWatcher(IdeFileExternalChangeHandler changeHandler, ProjectEvaluationService projectEvaluationService, ILogger<IdeFileWatcher> logger) : IDisposable
{
	private readonly IdeFileExternalChangeHandler _changeHandler = changeHandler;
	private readonly ProjectEvaluationService _projectEvaluationService = projectEvaluationService;
	private readonly ILogger<IdeFileWatcher> _logger = logger;
	private readonly Lock _lock = new();
	private Dictionary<string, FileSystemWatcher> _watchersByRootPath = new(PathComparison.Comparer);
	private SharpIdeFileTree? _fileTree;

	public void StartWatching(SharpIdeSolutionModel solution)
	{
		StopWatching();
		_fileTree = solution.FileTree;
		_fileTree.RootsChanged += UpdateWatchers;
		UpdateWatchers();
	}

	public void StopWatching()
	{
		lock (_lock)
		{
			_fileTree?.RootsChanged -= UpdateWatchers;
			_fileTree = null;
			foreach (var watcher in _watchersByRootPath.Values) watcher.Dispose();
			_watchersByRootPath.Clear();
		}
	}

	private void UpdateWatchers()
	{
		lock (_lock)
		{
			if (_fileTree is null) return;
			var watchersByRootPath = new Dictionary<string, FileSystemWatcher>(PathComparison.Comparer);
			var startedWatching = false;
			foreach (var root in _fileTree.Roots)
			{
				if (watchersByRootPath.ContainsKey(root.Path)) continue;
				if (_watchersByRootPath.Remove(root.Path, out var watcher) is false)
				{
					watcher = CreateWatcher(root.Path);
					if (watcher is null) continue;
					startedWatching = true;
				}
				watchersByRootPath[root.Path] = watcher;
			}
			foreach (var staleWatcher in _watchersByRootPath.Values) staleWatcher.Dispose();
			_watchersByRootPath = watchersByRootPath;
			// Anything that changed between the folder being read and now would otherwise be missed
			if (startedWatching) _changeHandler.RescanAll();
		}
	}

	private FileSystemWatcher? CreateWatcher(string rootPath)
	{
		try
		{
			var watcher = new FileSystemWatcher(rootPath)
			{
				IncludeSubdirectories = true,
				// The max recommended. Builds write a lot to bin/obj, which we can only filter out after the OS has buffered the events.
				InternalBufferSize = 64 * 1024,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};
			watcher.Changed += (_, e) => OnEvent(rootPath, e);
			watcher.Created += (_, e) => OnEvent(rootPath, e);
			watcher.Deleted += (_, e) => OnEvent(rootPath, e);
			watcher.Renamed += (_, e) => OnRenamed(rootPath, e);
			watcher.Error += (_, e) => OnError(rootPath, e);
			watcher.EnableRaisingEvents = true;
			return watcher;
		}
		catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
		{
			// e.g. a project folder deleted since the solution was read
			_logger.LogWarning(e, "IdeFileWatcher: Could not watch '{RootPath}'", rootPath);
			return null;
		}
	}

	private void OnEvent(string rootPath, FileSystemEventArgs e)
	{
		if (TreeMapperV2.IsInExcludedFolder(rootPath, e.FullPath))
		{
			// The obj/*.nuget.g.props/targets restore writes are imported by projects, though obj is otherwise ignored
			if (IsRestoreGeneratedImport(e.FullPath)) _projectEvaluationService.ImportFileChanged(e.FullPath);
			return;
		}
		_changeHandler.PathChanged(e.FullPath);
	}

	private static bool IsRestoreGeneratedImport(string path)
	{
		var fileName = Path.GetFileName(path.AsSpan());
		return fileName.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase);
	}

	private void OnRenamed(string rootPath, RenamedEventArgs e)
	{
		if (IsRestoreGeneratedImport(e.FullPath) && TreeMapperV2.IsInExcludedFolder(rootPath, e.FullPath)) _projectEvaluationService.ImportFileChanged(e.FullPath);
		var isOldPathExcluded = TreeMapperV2.IsInExcludedFolder(rootPath, e.OldFullPath);
		var isNewPathExcluded = TreeMapperV2.IsInExcludedFolder(rootPath, e.FullPath);
		if (isOldPathExcluded is false && isNewPathExcluded is false) _changeHandler.PathRenamed(e.OldFullPath, e.FullPath);
		else if (isOldPathExcluded is false) _changeHandler.PathChanged(e.OldFullPath); // Moved into an excluded folder - gone, as far as we're concerned
		else if (isNewPathExcluded is false) _changeHandler.PathChanged(e.FullPath);
	}

	private void OnError(string rootPath, ErrorEventArgs e)
	{
		// Typically the buffer overflowed, so events were lost
		_logger.LogWarning(e.GetException(), "IdeFileWatcher: Error watching '{RootPath}', rescanning", rootPath);
		_changeHandler.RescanAll();
	}

	public void Dispose() => StopWatching();
}
