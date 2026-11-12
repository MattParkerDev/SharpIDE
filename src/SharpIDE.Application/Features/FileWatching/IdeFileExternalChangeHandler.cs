using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileWatching;

/// Keeps the model (and so Roslyn and open editors) in sync with the disk.
/// File watcher events are only hints of where to look: each batch compares the disk against the model, like IntelliJ's VFS refresh and Watchman.
/// So the order events arrived in, and how the OS merged or split them, doesn't matter - delete+create, create+delete, double renames,
/// and deletes inside a deleted folder all fall out of comparing the current state. A lost event just means a rescan, through the same code.
/// Changes the IDE makes itself update the model (and record file stamps) first, so their events find nothing to do.
public class IdeFileExternalChangeHandler
{
	private readonly ILogger<IdeFileExternalChangeHandler> _logger;
	private readonly FileChangedService _fileChangedService;
	private readonly SharpIdeRootFolderModificationService _rootFolderModificationService;
	private readonly IdeFileSystemLock _fileSystemLock;
	private readonly AsyncBatchingWorkQueue<FileSystemHint> _hintQueue;
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	private SharpIdeRootFolder RootFolder => SolutionModel.RootFolder;

	private readonly record struct FileSystemHint(string Path, string? RenamedFromPath = null, bool RescanAll = false);

	public IdeFileExternalChangeHandler(FileChangedService fileChangedService, SharpIdeRootFolderModificationService rootFolderModificationService, IdeFileSystemLock fileSystemLock, ILogger<IdeFileExternalChangeHandler> logger)
	{
		_logger = logger;
		_fileChangedService = fileChangedService;
		_rootFolderModificationService = rootFolderModificationService;
		_fileSystemLock = fileSystemLock;
		// Deduplicates identical hints within a batch. A burst (e.g. a git checkout) settles into one batch.
		_hintQueue = new AsyncBatchingWorkQueue<FileSystemHint>(TimeSpan.FromMilliseconds(50), ProcessHintsAsync, EqualityComparer<FileSystemHint>.Default, IAsynchronousOperationListener.Instance, CancellationToken.None);
		GlobalEvents.Instance.AnalyzerDllsChanged.Subscribe(OnAnalyzerDllsChanged);
	}

	/// Something at this path may have been created, changed or deleted - a file or a folder
	public void PathChanged(string path) => _hintQueue.AddWork(new FileSystemHint(path));
	/// Applied as a rename if the old path is gone and the new one exists, so e.g. an open tab follows the file. Otherwise both paths are checked.
	public void PathRenamed(string oldPath, string newPath) => _hintQueue.AddWork(new FileSystemHint(newPath, RenamedFromPath: oldPath));
	/// Compare the whole tree - e.g. events were lost, or watching has just started
	public void RescanAll() => _hintQueue.AddWork(new FileSystemHint("", RescanAll: true));

	private async ValueTask ProcessHintsAsync(ImmutableSegmentedList<FileSystemHint> hints, CancellationToken cancellationToken)
	{
		if (SolutionModel is null) return;
		using var _ = await _fileSystemLock.Lock.LockAsync(cancellationToken);
		using var __ = _fileChangedService.BeginBatch(); // One SolutionAltered for the whole batch
		var reconcile = new Reconcile(this);

		if (hints.Any(h => h.RescanAll))
		{
			await reconcile.Folder(RootFolder);
		}
		else
		{
			var pathsToCheck = new HashSet<string>(StringComparer.Ordinal);
			foreach (var hint in hints)
			{
				if (hint.RenamedFromPath is { } oldPath && await reconcile.TryRename(oldPath, hint.Path) is false)
				{
					pathsToCheck.Add(oldPath);
				}
				pathsToCheck.Add(hint.Path);
			}
			// Parents first, so an added folder is in the model before its children are checked (and then found to already be there)
			foreach (var path in pathsToCheck.OrderBy(p => p.Length))
			{
				await reconcile.Path(path);
			}
		}

		await ApplyContentChanges(reconcile.ChangedFiles);
		if (reconcile.OperationCount > 0) _logger.LogInformation("IdeFileExternalChangeHandler: Reconciled {HintCount} hints - {OperationCount} tree changes, {ChangedFileCount} changed files", hints.Count, reconcile.OperationCount, reconcile.ChangedFiles.Count);
	}

	/// Reads changed files, and hands them on together - closed files reach Roslyn in one workspace change, open files are reloaded via their editors
	private async Task ApplyContentChanges(IReadOnlyList<SharpIdeFile> changedFiles)
	{
		if (changedFiles.Count is 0) return;
		var readResults = await Task.WhenAll(changedFiles.Select(async file =>
		{
			try
			{
				// Stamp first: if the file changes while we read it, the stamp won't match the new contents' event, so it is read again
				var stamp = FileStamp.TryRead(file.Path);
				var contents = await File.ReadAllTextAsync(file.Path);
				return (File: file, Stamp: stamp, Contents: (string?)contents);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// Typically deleted, or still locked by the writer - which will raise another event once done
				_logger.LogWarning(e, "IdeFileExternalChangeHandler: Could not read changed file '{FilePath}'", file.Path);
				return (File: file, Stamp: (FileStamp?)null, Contents: (string?)null);
			}
		}));
		var readFiles = readResults.Where(s => s.Contents is not null).ToList();
		foreach (var (file, stamp, _) in readFiles) file.DiskStamp = stamp;
		if (readFiles.Count is 0) return;
		await _fileChangedService.ExternalFilesChanged(readFiles.Select(s => (s.File, s.Contents!)).ToList());
	}

	/// One batch's comparison of disk against the model
	private sealed class Reconcile(IdeFileExternalChangeHandler handler)
	{
		private readonly SharpIdeRootFolder _root = handler.RootFolder;
		private readonly SharpIdeRootFolderModificationService _modificationService = handler._rootFolderModificationService;
		private readonly ILogger _logger = handler._logger;
		private Dictionary<string, SharpIdeFolder>? _foldersByPath;

		public List<SharpIdeFile> ChangedFiles { get; } = [];
		public int OperationCount { get; private set; }

		public async Task<bool> TryRename(string oldPath, string newPath)
		{
			if (File.Exists(oldPath) || Directory.Exists(oldPath)) return false; // Not a plain rename (e.g. something was put back) - just check both paths
			var newParent = FindFolder(System.IO.Path.GetDirectoryName(newPath)!);
			if (newParent is null) return false;
			var newName = System.IO.Path.GetFileName(newPath);
			try
			{
				if (_root.AllFiles.TryGetValue(oldPath, out var file))
				{
					if (_root.AllFiles.ContainsKey(newPath) || File.Exists(newPath) is false) return false;
					if (file.Parent != newParent) await _modificationService.MoveFile(newParent, file);
					if (file.Name.Value != newName) await _modificationService.RenameFile(file, newName);
					OperationCount++;
					return true;
				}
				if (FindFolder(oldPath) is { } folder && folder != _root)
				{
					if (FindFolder(newPath) is not null || Directory.Exists(newPath) is false) return false;
					if (folder.Parent != newParent) await _modificationService.MoveDirectory(newParent, folder);
					if (folder.Name.Value != newName) await _modificationService.RenameDirectory(folder, newName);
					_foldersByPath = null; // Paths below it changed
					OperationCount++;
					return true;
				}
			}
			catch (Exception e)
			{
				_logger.LogError(e, "IdeFileExternalChangeHandler: Failed to apply rename '{OldPath}' -> '{NewPath}'", oldPath, newPath);
			}
			return false;
		}

		/// Brings a single path in the model in line with the disk. Folder contents are hinted separately (or rescanned), so are not walked.
		public async Task Path(string path)
		{
			try
			{
				var modelFile = _root.AllFiles.GetValueOrDefault(path);
				var modelFolder = modelFile is null ? FindFolder(path) : null;
				var isFileOnDisk = File.Exists(path);
				var isFolderOnDisk = isFileOnDisk is false && Directory.Exists(path);

				if (modelFile is not null)
				{
					if (isFileOnDisk)
					{
						if (HasChanged(modelFile, FileStamp.TryRead(path))) ChangedFiles.Add(modelFile);
						return;
					}
					await RemoveFile(modelFile);
				}
				else if (modelFolder is not null)
				{
					if (isFolderOnDisk || modelFolder == _root) return;
					await RemoveFolder(modelFolder);
				}

				if (isFileOnDisk) await AddFile(path);
				else if (isFolderOnDisk) await AddFolder(path);
			}
			catch (Exception e)
			{
				_logger.LogError(e, "IdeFileExternalChangeHandler: Failed to reconcile '{Path}'", path);
			}
		}

		/// Compares a folder's whole subtree against the disk - used when events may have been lost
		public async Task Folder(SharpIdeFolder folder)
		{
			try
			{
				var directoryInfo = new DirectoryInfo(folder.Path);
				if (directoryInfo.Exists is false) return; // Its parent's comparison removes it
				var diskFiles = directoryInfo.EnumerateFiles().ToDictionary(f => f.FullName, StringComparer.Ordinal);
				var diskFolderPaths = directoryInfo.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
					.Where(d => TreeMapperV2.IsExcludedFolderName(d.Name) is false)
					.Select(d => d.FullName)
					.ToHashSet(StringComparer.Ordinal);

				foreach (var file in folder.Files.ToList())
				{
					if (diskFiles.Remove(file.Path, out var fileInfo))
					{
						if (HasChanged(file, FileStamp.From(fileInfo))) ChangedFiles.Add(file);
					}
					else await RemoveFile(file);
				}
				foreach (var newFilePath in diskFiles.Keys) await AddFile(newFilePath);

				foreach (var subFolder in folder.Folders.ToList())
				{
					if (diskFolderPaths.Remove(subFolder.Path)) await Folder(subFolder);
					else await RemoveFolder(subFolder);
				}
				foreach (var newFolderPath in diskFolderPaths) await AddFolder(newFolderPath); // Enumerates its contents
			}
			catch (Exception e)
			{
				_logger.LogError(e, "IdeFileExternalChangeHandler: Failed to rescan folder '{FolderPath}'", folder.Path);
			}
		}

		// No stamp means we don't know what's on disk - read it, and let the content comparison (Roslyn, the editor) decide
		private static bool HasChanged(SharpIdeFile file, FileStamp? diskStamp) => file.DiskStamp is null || diskStamp != file.DiskStamp;

		private async Task AddFile(string path)
		{
			var parent = FindFolder(System.IO.Path.GetDirectoryName(path)!);
			if (parent is null) return; // Inside a folder we don't show (or one that is about to be added, along with its contents)
			string contents;
			FileStamp? stamp;
			try
			{
				stamp = FileStamp.TryRead(path);
				contents = await File.ReadAllTextAsync(path);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// Typically still locked by the writer, which will raise another event once done
				_logger.LogWarning(e, "IdeFileExternalChangeHandler: Could not read created file '{FilePath}'", path);
				return;
			}
			await _modificationService.CreateFile(parent, path, System.IO.Path.GetFileName(path), contents, stamp);
			OperationCount++;
		}

		private async Task AddFolder(string path)
		{
			var parent = FindFolder(System.IO.Path.GetDirectoryName(path)!);
			if (parent is null) return;
			await _modificationService.AddDirectory(parent, System.IO.Path.GetFileName(path));
			_foldersByPath = null;
			OperationCount++;
		}

		private async Task RemoveFile(SharpIdeFile file)
		{
			await _modificationService.RemoveFile(file);
			OperationCount++;
		}

		private async Task RemoveFolder(SharpIdeFolder folder)
		{
			await _modificationService.RemoveDirectory(folder);
			_foldersByPath = null;
			OperationCount++;
		}

		private SharpIdeFolder? FindFolder(string path)
		{
			if (string.Equals(path, _root.Path, StringComparison.Ordinal)) return _root;
			if (_foldersByPath is null)
			{
				_foldersByPath = new Dictionary<string, SharpIdeFolder>(StringComparer.Ordinal);
				foreach (var folder in _root.AllFolders) _foldersByPath.TryAdd(folder.Path, folder);
			}
			return _foldersByPath.GetValueOrDefault(path);
		}
	}

	private async Task OnAnalyzerDllsChanged(ImmutableArray<string> analyzerDllPaths)
	{
		using var _ = await _fileSystemLock.Lock.LockAsync();
		await _fileChangedService.AnalyzerDllFilesChanged(analyzerDllPaths);
	}
}
