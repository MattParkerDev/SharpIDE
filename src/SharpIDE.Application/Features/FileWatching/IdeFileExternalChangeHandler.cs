using System.Collections.Immutable;
using Ardalis.GuardClauses;
using Microsoft.Extensions.Logging;
using NeoSmart.AsyncLock;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileExternalChangeHandler
{
	private readonly ILogger<IdeFileExternalChangeHandler> _logger;
	private readonly FileChangedService _fileChangedService;
	private readonly SharpIdeRootFolderModificationService _rootFolderModificationService;
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	private SharpIdeRootFolder RootFolder => SolutionModel.RootFolder;
	public AsyncLock IdeChangeLock { get; } = new AsyncLock();

	public IdeFileExternalChangeHandler(FileChangedService fileChangedService, SharpIdeRootFolderModificationService rootFolderModificationService, ILogger<IdeFileExternalChangeHandler> logger)
	{
		_logger = logger;
		_fileChangedService = fileChangedService;
		_rootFolderModificationService = rootFolderModificationService;
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

	private async Task OnFileChanged(string filePath)
	{
		using var _ = await IdeChangeLock.LockAsync();
		var sharpIdeFile = RootFolder.AllFiles.GetValueOrDefault(filePath);
		if (sharpIdeFile is null) return;
		if (sharpIdeFile.SuppressDiskChangeEvents is true) return;
		if (sharpIdeFile.LastIdeWriteTime is not null)
		{
			var now = DateTimeOffset.Now;
			if (now - sharpIdeFile.LastIdeWriteTime.Value < TimeSpan.FromMilliseconds(300))
			{
				_logger.LogTrace("File change ignored - recently modified by the IDE: '{FilePath}'", filePath);
				return;
			}
		}
		_logger.LogInformation("IdeFileExternalChangeHandler: Changed - '{FilePath}'", filePath);
		if (sharpIdeFile is not null)
		{
			await _fileChangedService.SharpIdeFileChanged(sharpIdeFile, await File.ReadAllTextAsync(sharpIdeFile.Path), FileChangeType.ExternalChange);
		}
	}

	private async Task OnAnalyzerDllsChanged(ImmutableArray<string> analyzerDllPaths)
	{
		using var _ = await IdeChangeLock.LockAsync();
		await _fileChangedService.AnalyzerDllFilesChanged(analyzerDllPaths);
	}
}
