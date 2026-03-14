using System.Text;
using SharpIDE.Application.Features.Events;

namespace SharpIDE.Application.Features.Git;

public sealed class GitRepositoryMonitor : IDisposable
{
    private FileSystemWatcher? _workingTreeWatcher;
    private FileSystemWatcher? _metadataWatcher;
    private string _repoRoot = string.Empty;
    private string _gitDirectoryPath = string.Empty;

    public EventWrapper<Task> RepositoryChanged { get; } = new(() => Task.CompletedTask);

    public void Start(string repoRoot, string gitDirectoryPath)
    {
        if (string.Equals(_repoRoot, repoRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_gitDirectoryPath, gitDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Stop();
        _repoRoot = NormalizePath(repoRoot);
        _gitDirectoryPath = NormalizePath(gitDirectoryPath);

        _workingTreeWatcher = CreateWatcher(_repoRoot, includeSubdirectories: true, OnWorkingTreeChanged);
        _metadataWatcher = CreateWatcher(_gitDirectoryPath, includeSubdirectories: true, OnMetadataChanged);
    }

    public void Stop()
    {
        DisposeWatcher(_workingTreeWatcher);
        DisposeWatcher(_metadataWatcher);
        _workingTreeWatcher = null;
        _metadataWatcher = null;
        _repoRoot = string.Empty;
        _gitDirectoryPath = string.Empty;
    }

    private void OnWorkingTreeChanged(object sender, FileSystemEventArgs args)
    {
        var fullPath = NormalizePath(args.FullPath);
        var dotGitPath = Path.Combine(_repoRoot, ".git");
        if (fullPath.Equals(NormalizePath(dotGitPath), StringComparison.OrdinalIgnoreCase)) return;
        if (fullPath.StartsWith(NormalizePath(dotGitPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        RepositoryChanged.InvokeParallelFireAndForget();
    }

    private void OnMetadataChanged(object sender, FileSystemEventArgs args)
    {
        var fullPath = NormalizePath(args.FullPath);
        var headPath = Path.Combine(_gitDirectoryPath, "HEAD");
        var fetchHeadPath = Path.Combine(_gitDirectoryPath, "FETCH_HEAD");
        var indexPath = Path.Combine(_gitDirectoryPath, "index");
        var packedRefsPath = Path.Combine(_gitDirectoryPath, "packed-refs");
        var logHeadPath = Path.Combine(_gitDirectoryPath, "logs", "HEAD");
        var refsHeadsPath = Path.Combine(_gitDirectoryPath, "refs", "heads");
        var refsRemotesPath = Path.Combine(_gitDirectoryPath, "refs", "remotes");
        var refsTagsPath = Path.Combine(_gitDirectoryPath, "refs", "tags");
        var logRefsRemotesPath = Path.Combine(_gitDirectoryPath, "logs", "refs", "remotes");

        if (fullPath.Equals(NormalizePath(headPath), StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(NormalizePath(fetchHeadPath), StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(NormalizePath(indexPath), StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(NormalizePath(packedRefsPath), StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(NormalizePath(logHeadPath), StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(NormalizePath(refsHeadsPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(NormalizePath(refsRemotesPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(NormalizePath(refsTagsPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(NormalizePath(logRefsRemotesPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            RepositoryChanged.InvokeParallelFireAndForget();
        }
    }

    private static FileSystemWatcher CreateWatcher(string path, bool includeSubdirectories, FileSystemEventHandler onChanged)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
        };
        watcher.Changed += onChanged;
        watcher.Created += onChanged;
        watcher.Deleted += onChanged;
        watcher.Renamed += (_, args) => onChanged(_, args);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public void Dispose()
    {
        Stop();
    }
}
