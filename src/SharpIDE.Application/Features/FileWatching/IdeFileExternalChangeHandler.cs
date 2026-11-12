using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using Microsoft.Extensions.Logging;
using NeoSmart.AsyncLock;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileExternalChangeHandler
{
	private readonly ILogger<IdeFileExternalChangeHandler> _logger;
	private readonly FileChangedService _fileChangedService;
	private readonly SharpIdeRootFolderModificationService _rootFolderModificationService;
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	private SharpIdeRootFolder RootFolder => SolutionModel.RootFolder;
	public AsyncLock IdeChangeLock { get; } = new AsyncLock();
	private readonly AsyncBatchingWorkQueue<string> _fileChangedQueue;

	public IdeFileExternalChangeHandler(FileChangedService fileChangedService, SharpIdeRootFolderModificationService rootFolderModificationService, ILogger<IdeFileExternalChangeHandler> logger)
	{
		_logger = logger;
		_fileChangedService = fileChangedService;
		_rootFolderModificationService = rootFolderModificationService;
		// Deduplicates paths within a batch
		_fileChangedQueue = new AsyncBatchingWorkQueue<string>(TimeSpan.FromMilliseconds(50), ProcessFileChangesAsync, StringComparer.Ordinal, IAsynchronousOperationListener.Instance, CancellationToken.None);
		GlobalEvents.Instance.FileSystemWatcherInternal.FileChanged.Subscribe(OnFileChanged);
		GlobalEvents.Instance.FileSystemWatcherInternal.FileCreated.Subscribe(OnFileCreated);
		GlobalEvents.Instance.FileSystemWatcherInternal.FileDeleted.Subscribe(OnFileDeleted);
		GlobalEvents.Instance.FileSystemWatcherInternal.FileRenamed.Subscribe(OnFileRenamed);
		GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryCreated.Subscribe(OnFolderCreated);
		GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryDeleted.Subscribe(OnFolderDeleted);
		GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryRenamed.Subscribe(OnFolderRenamed);
		GlobalEvents.Instance.AnalyzerDllsChanged.Subscribe(OnAnalyzerDllsChanged);
	}

	private async Task OnFileRenamed(string oldFilePath, string newFilePath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFile = RootFolder.AllFiles.GetValueOrDefault(oldFilePath);
		if (sharpIdeFile is null) return;
		await _rootFolderModificationService.RenameFile(sharpIdeFile, Path.GetFileName(newFilePath));
	}

	private async Task OnFileDeleted(string filePath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFile = RootFolder.AllFiles.GetValueOrDefault(filePath);
		if (sharpIdeFile is null) return;
		await _rootFolderModificationService.RemoveFile(sharpIdeFile);
	}

	// TODO: Test - this most likely only will ever be called on linux - windows and macos(?) does delete + create on rename of folders
	private async Task OnFolderRenamed(string oldFolderPath, string newFolderPath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFolder = RootFolder.AllFolders.SingleOrDefault(f => f.Path == oldFolderPath);
		if (sharpIdeFolder is null)
		{
			return;
		}
		var isMoveRatherThanRename = Path.GetDirectoryName(oldFolderPath) != Path.GetDirectoryName(newFolderPath);
		if (isMoveRatherThanRename)
		{
			var newParentPath = Path.GetDirectoryName(newFolderPath)!;
			var destinationParent = RootFolder.AllFolders.SingleOrDefault(f => f.Path == newParentPath) ?? (newParentPath == RootFolder.Path ? RootFolder : throw new InvalidOperationException($"Destination parent folder '{newParentPath}' of moved folder '{oldFolderPath}' does not exist in the SharpIdeRootFolder"));
			await _rootFolderModificationService.MoveDirectory(destinationParent, sharpIdeFolder);
		}
		else
		{
			var newFolderName = Path.GetFileName(newFolderPath);
			await _rootFolderModificationService.RenameDirectory(sharpIdeFolder, newFolderName);
		}
	}

	private async Task OnFolderDeleted(string folderPath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFolder = RootFolder.AllFolders.SingleOrDefault(f => f.Path == folderPath);
		if (sharpIdeFolder is null)
		{
			return;
		}
		await _rootFolderModificationService.RemoveDirectory(sharpIdeFolder);
	}

	private async Task OnFolderCreated(string folderPath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFolder = RootFolder.AllFolders.SingleOrDefault(f => f.Path == folderPath);
		if (sharpIdeFolder is not null)
		{
			return;
		}
		var containingFolderPath = Path.GetDirectoryName(folderPath)!;
		var containingFolder = RootFolder.AllFolders.SingleOrDefault(f => f.ChildNodeBasePath == containingFolderPath)
		                       ?? (containingFolderPath == RootFolder.Path ? (SharpIdeFolder)RootFolder : null);
		if (containingFolder is null)
		{
			_logger.LogError("Containing folder of folder '{FolderPath}' does not exist", folderPath);
			return;
		}
		var folderName = Path.GetFileName(folderPath);
		await _rootFolderModificationService.AddDirectory(containingFolder, folderName);
	}

	private async Task OnFileCreated(string filePath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFile = RootFolder.AllFiles.GetValueOrDefault(filePath);
		if (sharpIdeFile is not null)
		{
			// It was likely already created via a parent folder creation
			return;
		}
		var createdFileDirectory = Path.GetDirectoryName(filePath)!;
		var containingFolder = RootFolder.AllFolders.SingleOrDefault(f => f.ChildNodeBasePath == createdFileDirectory)
		                       ?? (createdFileDirectory == RootFolder.Path ? (SharpIdeFolder)RootFolder : null);
		if (containingFolder is null)
		{
			_logger.LogError("Containing folder of file '{FilePath}' does not exist", filePath);
			return;
		}
		await _rootFolderModificationService.CreateFile(containingFolder, filePath, Path.GetFileName(filePath), await File.ReadAllTextAsync(filePath));
	}

	// Our own writes are filtered out when the event arrives, as that's when SuppressDiskChangeEvents/LastIdeWriteTime are meaningful
	private Task OnFileChanged(string filePath)
	{
		var sharpIdeFile = RootFolder.AllFiles.GetValueOrDefault(filePath);
		if (sharpIdeFile is null) return Task.CompletedTask;
		if (sharpIdeFile.SuppressDiskChangeEvents is true) return Task.CompletedTask;
		if (sharpIdeFile.LastIdeWriteTime is not null && DateTimeOffset.Now - sharpIdeFile.LastIdeWriteTime.Value < TimeSpan.FromMilliseconds(300))
		{
			_logger.LogTrace("File change ignored - recently modified by the IDE: '{FilePath}'", filePath);
			return Task.CompletedTask;
		}
		_fileChangedQueue.AddWork(filePath);
		return Task.CompletedTask;
	}

	/// A burst of changes (e.g. a git checkout, or pasting over several files - and the several Changed events a single write often raises) is handled as one batch.
	/// Each file is read once, and closed files reach Roslyn in a single workspace change.
	private async ValueTask ProcessFileChangesAsync(ImmutableSegmentedList<string> filePaths, CancellationToken cancellationToken)
	{
		using var _ = await IdeChangeLock.LockAsync(cancellationToken);
		var files = filePaths
			.Select(path => RootFolder.AllFiles.GetValueOrDefault(path))
			.OfType<SharpIdeFile>() // May have been deleted or renamed since
			.ToList();
		var readResults = await Task.WhenAll(files.Select(async file =>
		{
			try
			{
				return (File: file, Contents: await File.ReadAllTextAsync(file.Path, cancellationToken));
			}
			catch (IOException e)
			{
				// Typically deleted, or still locked by the writer - which will raise another Changed event once done
				_logger.LogWarning(e, "IdeFileExternalChangeHandler: Could not read changed file '{FilePath}'", file.Path);
				return (File: file, Contents: (string?)null);
			}
		}));
		var changedFiles = readResults
			.Where(s => s.Contents is not null)
			.Select(s => (s.File, s.Contents!))
			.ToList();
		if (changedFiles.Count is 0) return;
		_logger.LogInformation("IdeFileExternalChangeHandler: Changed - {FileCount} files: {FilePaths}", changedFiles.Count, string.Join(", ", changedFiles.Select(s => s.File.Path)));
		await _fileChangedService.ExternalFilesChanged(changedFiles);
	}

	private async Task OnAnalyzerDllsChanged(ImmutableArray<string> analyzerDllPaths)
	{
		using var _ = await IdeChangeLock.LockAsync();
		await _fileChangedService.AnalyzerDllFilesChanged(analyzerDllPaths);
	}
}
