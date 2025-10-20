using Ardalis.GuardClauses;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public class IdeFileExternalChangeHandler
{
	private readonly FileChangedService _fileChangedService;
	private readonly SharpIdeSolutionModificationService _sharpIdeSolutionModificationService;
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;
	public IdeFileExternalChangeHandler(FileChangedService fileChangedService, SharpIdeSolutionModificationService sharpIdeSolutionModificationService)
	{
		_fileChangedService = fileChangedService;
		_sharpIdeSolutionModificationService = sharpIdeSolutionModificationService;
		GlobalEvents.Instance.FileSystemWatcherInternal.FileChanged.Subscribe(OnFileChanged);
		GlobalEvents.Instance.FileSystemWatcherInternal.FileCreated.Subscribe(OnFileCreated);
		GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryCreated.Subscribe(OnFolderCreated);
		GlobalEvents.Instance.FileSystemWatcherInternal.DirectoryDeleted.Subscribe(OnFolderDeleted);
	}

	private async Task OnFolderDeleted(string folderPath)
	{
		var sharpIdeFolder = SolutionModel.AllFolders.SingleOrDefault(f => f.Path == folderPath);
		if (sharpIdeFolder is null)
		{
			return;
		}
		await _sharpIdeSolutionModificationService.RemoveDirectory(sharpIdeFolder);
	}

	private async Task OnFolderCreated(string folderPath)
	{
		var sharpIdeFolder = SolutionModel.AllFolders.SingleOrDefault(f => f.Path == folderPath);
		if (sharpIdeFolder is not null)
		{
			//Console.WriteLine($"Error - Folder {folderPath} already exists");
			return;
		}
		var containingFolderPath = Path.GetDirectoryName(folderPath)!;
		var containingFolderOrProject = (IFolderOrProject?)SolutionModel.AllFolders.SingleOrDefault(f => f.ChildNodeBasePath == containingFolderPath) ?? SolutionModel.AllProjects.SingleOrDefault(s => s.ChildNodeBasePath == containingFolderPath);
		if (containingFolderOrProject is null)
		{
			Console.WriteLine($"Error - Containing Folder or Project of {folderPath} does not exist");
			return;
		}
		var folderName = Path.GetFileName(folderPath);
		await _sharpIdeSolutionModificationService.AddDirectory(containingFolderOrProject, folderName);
	}

	private async Task OnFileCreated(string filePath)
	{
		// Create a new sharpIdeFile, update SolutionModel
		var sharpIdeFile = SolutionModel.AllFiles.SingleOrDefault(f => f.Path == filePath);
		if (sharpIdeFile is not null)
		{
			// It was likely already created via a parent folder creation
			return;
		}
		// If sharpIdeFile is null, it means the file was created externally, and we need to create it and add it to the solution model
		var createdFileDirectory = Path.GetDirectoryName(filePath)!;

		var containingFolderOrProject = (IFolderOrProject?)SolutionModel.AllFolders.SingleOrDefault(f => f.ChildNodeBasePath == createdFileDirectory) ?? SolutionModel.AllProjects.SingleOrDefault(s => s.ChildNodeBasePath == createdFileDirectory);
		if (containingFolderOrProject is null)
		{
			Console.WriteLine($"Error - Containing Folder or Project of {filePath} does not exist");
			return;
		}

		sharpIdeFile = new SharpIdeFile(filePath, Path.GetFileName(filePath), containingFolderOrProject, []);
		containingFolderOrProject.Files.Add(sharpIdeFile);
		SolutionModel.AllFiles.Add(sharpIdeFile);

		await _fileChangedService.SharpIdeFileAdded(sharpIdeFile, await File.ReadAllTextAsync(filePath));
	}

	private async Task OnFileChanged(string filePath)
	{
		var sharpIdeFile = SolutionModel.AllFiles.SingleOrDefault(f => f.Path == filePath);
		if (sharpIdeFile is null) return;
		if (sharpIdeFile.SuppressDiskChangeEvents is true) return;
		if (sharpIdeFile.LastIdeWriteTime is not null)
		{
			var now = DateTimeOffset.Now;
			if (now - sharpIdeFile.LastIdeWriteTime.Value < TimeSpan.FromMilliseconds(300))
			{
				Console.WriteLine($"IdeFileExternalChangeHandler: Ignored - {filePath}");
				return;
			}
		}
		Console.WriteLine($"IdeFileExternalChangeHandler: Changed - {filePath}");
		var file = SolutionModel.AllFiles.SingleOrDefault(f => f.Path == filePath);
		if (file is not null)
		{
			await _fileChangedService.SharpIdeFileChanged(file, await File.ReadAllTextAsync(file.Path), FileChangeType.ExternalChange);
		}
	}
}
