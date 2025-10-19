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
		var containingFolder = SolutionModel.AllFolders.SingleOrDefault(f => f.Path == containingFolderPath);
		if (containingFolder is null)
		{
			Console.WriteLine($"Error - Containing Folder of {folderPath} does not exist");
			return;
		}
		var folderName = Path.GetFileName(folderPath);
		await _sharpIdeSolutionModificationService.AddDirectory(containingFolder, folderName);
	}

	private async Task OnFileCreated(string filePath)
	{
		// Create a new sharpIdeFile, update SolutionModel
		var sharpIdeFile = SolutionModel.AllFiles.SingleOrDefault(f => f.Path == filePath);
		if (sharpIdeFile == null)
		{
			// If sharpIdeFile is null, it means the file was created externally, and we need to create it and add it to the solution model
			var createdFileDirectory = Path.GetDirectoryName(filePath)!;

			// TODO: Handle being contained by a project directly
			//var containingProject = SolutionModel.AllProjects.SingleOrDefault(p => createdFileDirectory == Path.GetDirectoryName(p.FilePath));
			var containingFolder = SolutionModel.AllFolders.SingleOrDefault(f => f.Path == createdFileDirectory);
			if (containingFolder is null)
			{
				// TODO: Create the folder and add it to the solution model
			}

			sharpIdeFile = new SharpIdeFile(filePath, Path.GetFileName(filePath), containingFolder, []);
			containingFolder.Files.Add(sharpIdeFile);
			SolutionModel.AllFiles.Add(sharpIdeFile);
			// sharpIdeFile = TODO;
		}
		Guard.Against.Null(sharpIdeFile, nameof(sharpIdeFile));
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
