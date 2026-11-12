using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace SharpIDE.Application.Features.FileSystem;

public class FileSystemService
{
	public static async Task<SharpIdeRootFolder> GetSharpIdeRootFolderForSolutionAsync(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionFilePath))!;
		if (Directory.Exists(solutionDirectory) is false) throw new DirectoryNotFoundException();
		var rootFolderPath = await GetRootFolderPath(solutionFilePath, solutionDirectory, cancellationToken);
		var rootFolder = new SharpIdeRootFolder(new DirectoryInfo(rootFolderPath));
		return rootFolder;
	}

	/// The solution's directory, widened to the closest common ancestor of any projects outside it (e.g. src/App.sln referencing ../tests/App.Tests.csproj),
	/// so every project's folder is in the one tree, which the one watcher covers - like opening the repository folder in VS Code.
	/// Never widened to a drive root (e.g. a project on another drive), as that would mean enumerating and watching everything. Such a project loads without a folder.
	private static async Task<string> GetRootFolderPath(string solutionFilePath, string solutionDirectory, CancellationToken cancellationToken)
	{
		var solutionSerializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
		if (solutionSerializer is null) return solutionDirectory;
		Microsoft.VisualStudio.SolutionPersistence.Model.SolutionModel vsSolution;
		try
		{
			vsSolution = await solutionSerializer.OpenAsync(solutionFilePath, cancellationToken);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			return solutionDirectory; // Reading the solution properly reports the problem
		}

		var rootFolderPath = solutionDirectory;
		foreach (var vsProject in vsSolution.SolutionProjects.Where(p => p.Extension is ".csproj"))
		{
			// Resolved as IntermediateMapper does
			var projectDirectory = Path.GetDirectoryName(new DirectoryInfo(Path.Join(solutionDirectory, vsProject.FilePath)).FullName)!;
			if (PathComparison.IsSameOrUnder(projectDirectory, rootFolderPath)) continue;
			var commonAncestor = GetCommonAncestor(rootFolderPath, projectDirectory);
			if (commonAncestor is null || PathComparison.PathEquals(Path.GetPathRoot(commonAncestor), commonAncestor)) continue;
			rootFolderPath = commonAncestor;
		}
		return rootFolderPath;
	}

	private static string? GetCommonAncestor(string folderPath, string otherFolderPath)
	{
		var current = folderPath;
		while (current is not null && PathComparison.IsSameOrUnder(otherFolderPath, current) is false)
		{
			current = Path.GetDirectoryName(current);
		}
		return current;
	}
}
