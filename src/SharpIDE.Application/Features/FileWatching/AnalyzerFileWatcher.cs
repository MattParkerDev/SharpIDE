using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FileSystem;

namespace SharpIDE.Application.Features.FileWatching;

public class AnalyzerFileWatcher
{
	private readonly AsyncBatchingWorkQueue<string> _fileChangedQueue;
	private readonly Lock _lock = new();
	private List<FileSystemWatcher> _fileWatchers = [];

	public AnalyzerFileWatcher()
	{
		_fileChangedQueue = new AsyncBatchingWorkQueue<string>(
			TimeSpan.FromMilliseconds(500),
			async (filePaths, ct) => await GlobalEvents.Instance.AnalyzerDllsChanged.InvokeParallelAsync(filePaths.ToImmutableArray()),
			PathComparison.Comparer,
			new AsynchronousOperationListenerProvider.NullOperationListener(),
			CancellationToken.None);
	}

	/// The analyzer dlls are spread across directories (e.g. NuGet packages), so one non-recursive watcher per directory, filtered to just those files
	public Task StartWatchingFiles(ImmutableArray<string> filePaths)
	{
		var newWatchers = filePaths
			.GroupBy(path => Path.GetDirectoryName(path)!, PathComparison.Comparer)
			.Where(group => Directory.Exists(group.Key))
			.Select(group =>
			{
				var watcher = new FileSystemWatcher(group.Key)
				{
					// Builds may replace a dll (delete + create, or rename over it) rather than write to it
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
				};
				foreach (var path in group) watcher.Filters.Add(Path.GetFileName(path));
				watcher.Changed += OnFileChanged;
				watcher.Created += OnFileChanged;
				watcher.Renamed += OnFileChanged;
				watcher.EnableRaisingEvents = true;
				return watcher;
			})
			.ToList();

		List<FileSystemWatcher> oldWatchers;
		lock (_lock)
		{
			oldWatchers = _fileWatchers;
			_fileWatchers = newWatchers;
		}
		foreach (var watcher in oldWatchers) watcher.Dispose();
		return Task.CompletedTask;
	}

	private void OnFileChanged(object sender, FileSystemEventArgs e) => _fileChangedQueue.AddWork(e.FullPath);
}
