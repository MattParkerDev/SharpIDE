using Ardalis.GuardClauses;
using LibGit2Sharp;
using Microsoft.CodeAnalysis.Razor.Utilities;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.FileWatching;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public class SharpIdeSolutionService(RoslynAnalysis roslynAnalysis)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;

	private SharpIdeSolutionModel? _sharpIdeSolutionModel;
	private SolutionModel? _vsSolution;
	private ISolutionSerializer? _solutionSerializer;
	private string? _solutionFilePath;

	public async Task LoadSolution(SharpIdeSolutionModel sharpIdeSolutionModel, string solutionFilePath, SolutionModel vsSln, ISolutionSerializer slnSerializer, CancellationToken cancellationToken = default)
	{
		_vsSolution = vsSln;
		_solutionSerializer = slnSerializer;
		_solutionFilePath = solutionFilePath;
		_sharpIdeSolutionModel = sharpIdeSolutionModel;
	}

	public async Task ReloadSolution(CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_solutionFilePath);
		var (newSharpIdeSolutionModel, solutionModel, _) = await ReadSolution(_solutionFilePath, _sharpIdeSolutionModel!.FileTree, cancellationToken);
		_vsSolution = solutionModel;
		_sharpIdeSolutionModel!.UpdateFromNewSolution(newSharpIdeSolutionModel);
		await _roslynAnalysis.ReloadSolution(cancellationToken);
		await _roslynAnalysis.UpdateSolutionDiagnostics(cancellationToken);
	}

	/// Creates an empty solution. The format (.sln or .slnx) is chosen by the file extension
	public static async Task CreateSolution(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		Guard.Against.NullOrWhiteSpace(solutionFilePath);
		if (File.Exists(solutionFilePath)) throw new InvalidOperationException($"Solution {solutionFilePath} already exists.");
		var solutionSerializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
		Guard.Against.Null(solutionSerializer);

		Directory.CreateDirectory(Path.GetDirectoryName(solutionFilePath)!);
		await solutionSerializer.SaveAsync(solutionFilePath, new SolutionModel(), cancellationToken);
	}

	// Weird separation between ReadSolution and LoadSolution is so we can call the static ReadSolution in IdeRoot before all the UI Nodes are ready and DI services injected
	public static async Task<(SharpIdeSolutionModel, SolutionModel, ISolutionSerializer)> ReadSolution(string solutionFilePath, SharpIdeFileTree fileTree, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity();

		ISolutionSerializer? solutionSerializer;
		SolutionModel vsSolution;
		using (SharpIdeOtel.Source.StartActivity("VsPersistence.OpenSolution"))
		{
			solutionSerializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
			Guard.Against.Null(solutionSerializer);
			vsSolution = await solutionSerializer.OpenAsync(solutionFilePath, cancellationToken);
		}

		// Before the projects are created, as they look up their folders in the tree. Resolved as IntermediateMapper does.
		var solutionDirectory = Path.GetDirectoryName(solutionFilePath);
		fileTree.UpdateRoots(solutionFilePath, vsSolution.SolutionProjects
			.Where(p => p.Extension is ".csproj")
			.Select(p => new DirectoryInfo(Path.Join(solutionDirectory, p.FilePath)).FullName));

		// This intermediate model is pretty much useless, but I have left it around as we grab the project nodes with it, which we might use later.
		var intermediateModel = await IntermediateMapper.GetIntermediateModel(solutionFilePath, vsSolution, cancellationToken);

		var solutionModel = new SharpIdeSolutionModel(solutionFilePath, intermediateModel, fileTree);
		SetGitStatuses(solutionModel);
		return (solutionModel, vsSolution, solutionSerializer);
	}

	/// A root outside the solution's repository (e.g. a project in another repository) has its own
	private static void SetGitStatuses(SharpIdeSolutionModel solutionModel)
	{
		var gitFolderPaths = solutionModel.FileTree.Roots
			.Select(r => Repository.Discover(r.Path))
			.OfType<string>()
			.Distinct(PathComparison.Comparer);
		foreach (var gitFolderPath in gitFolderPaths)
		{
			using var repo = new Repository(gitFolderPath);
			var status = repo.RetrieveStatus(new StatusOptions());

			foreach (var entry in status.Where(s => s.State is not FileStatus.Ignored))
			{
				// Relative to the repository root, which may be above the solution's directory
				var filePath = new FileInfo(Path.Combine(repo.Info.WorkingDirectory, entry.FilePath)).FullName; // used to normalise path separators
				var fileInSolution = solutionModel.AllFiles.GetValueOrDefault(filePath);
				if (fileInSolution is null) continue;

				var mappedGitStatus = entry.State switch
				{
					FileStatus.NewInIndex | FileStatus.ModifiedInWorkdir => GitFileStatus.Added, // I've seen these appear together
					FileStatus.NewInIndex or FileStatus.NewInWorkdir => GitFileStatus.Added,
					FileStatus.ModifiedInIndex or FileStatus.ModifiedInWorkdir => GitFileStatus.Modified,
					_ => GitFileStatus.Unaltered // TODO: handle other kinds?
				};

				fileInSolution.GitStatus = mappedGitStatus;
			}
		}
	}

	// This currently only adds the project to the VS Persistence solution, and persists it back to disk. We are currently relying on the sln change from disk, for
	// actually updating the SharpIdeSolutionModel
	public async Task AddProject(ISolutionOrSolutionFolder parentNode, string projectName, string projectFilePath, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.Null(parentNode);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);
		Guard.Against.NullOrWhiteSpace(projectName);
		Guard.Against.NullOrWhiteSpace(projectFilePath);

		SolutionFolderModel? vsSolutionFolder = null;
		if (parentNode is SharpIdeSolutionFolder solutionFolder)
		{
			vsSolutionFolder = _vsSolution.FindFolder(solutionFolder.VsPersistencePath);
		}

		// the project file path needs to be relative from the sln file directory
		var projectFileRelativePath = Path.GetRelativePath(Path.GetDirectoryName(_solutionFilePath)!, projectFilePath).Replace('\\', '/');

		_vsSolution.AddProject(projectFileRelativePath, null, vsSolutionFolder);
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
	}

	// Same as AddProject - we rely on the sln change from disk to update the SharpIdeSolutionModel
	public async Task RemoveProject(SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.Null(project);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);

		var solutionDirectory = Path.GetDirectoryName(_solutionFilePath)!;
		var vsProject = _vsSolution.SolutionProjects.SingleOrDefault(p => FilePathNormalizer.AreFilePathsEquivalent(Path.GetFullPath(Path.Combine(solutionDirectory, p.FilePath)), project.FilePath));
		Guard.Against.Null(vsProject);

		_vsSolution.RemoveProject(vsProject);
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
	}

	// Same as AddProject - we rely on the sln change from disk to update the SharpIdeSolutionModel
	// This also removes all child solution folders, projects and solution items from the solution
	public async Task RemoveSolutionFolder(SharpIdeSolutionFolder solutionFolder, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.Null(solutionFolder);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);

		var vsSolutionFolder = _vsSolution.FindFolder(solutionFolder.VsPersistencePath);
		Guard.Against.Null(vsSolutionFolder);

		_vsSolution.RemoveFolder(vsSolutionFolder);
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
	}

	// Same as AddProject - we rely on the sln change from disk to update the SharpIdeSolutionModel
	public async Task RemoveSolutionItemFile(SharpIdeSolutionItemFile solutionItemFile, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.Null(solutionItemFile);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);
		if (solutionItemFile.Parent is not SharpIdeSolutionFolder solutionFolder) throw new InvalidOperationException("Solution item files must be in a solution folder");

		var vsSolutionFolder = _vsSolution.FindFolder(solutionFolder.VsPersistencePath);
		Guard.Against.Null(vsSolutionFolder);
		var solutionDirectory = Path.GetDirectoryName(_solutionFilePath)!;
		var vsFile = vsSolutionFolder.Files?.SingleOrDefault(f => FilePathNormalizer.AreFilePathsEquivalent(Path.GetFullPath(Path.Combine(solutionDirectory, f)), solutionItemFile.Path));
		Guard.Against.Null(vsFile);

		vsSolutionFolder.RemoveFile(vsFile);
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
	}

	// Same as AddProject - we rely on the sln change from disk to update the SharpIdeSolutionModel
	// Call this after the file has been renamed on disk. Updates every solution folder that references the file
	public async Task RenameSolutionItemFile(string oldFilePath, string newFileName, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);
		Guard.Against.NullOrWhiteSpace(oldFilePath);
		Guard.Against.NullOrWhiteSpace(newFileName);

		var solutionDirectory = Path.GetDirectoryName(_solutionFilePath)!;
		var renamedAny = false;
		foreach (var vsSolutionFolder in _vsSolution.SolutionFolders)
		{
			var vsFiles = vsSolutionFolder.Files?.Where(f => FilePathNormalizer.AreFilePathsEquivalent(Path.GetFullPath(Path.Combine(solutionDirectory, f)), oldFilePath)).ToList() ?? [];
			foreach (var vsFile in vsFiles)
			{
				// Keep the existing entry's directory and separator style, only swap the file name
				var lastSeparatorIndex = Math.Max(vsFile.LastIndexOf('/'), vsFile.LastIndexOf('\\'));
				vsSolutionFolder.RemoveFile(vsFile);
				vsSolutionFolder.AddFile($"{vsFile[..(lastSeparatorIndex + 1)]}{newFileName}");
				renamedAny = true;
			}
		}
		if (renamedAny is false) return;
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
	}

	/// Call after the IDE has moved or renamed a project file, or a folder containing project files, on disk - so the solution keeps referencing them, as in VS.
	/// Unlike the methods above, the caller reloads the solution (the moved csproj triggers it, once its batch ends), so the save's stamp is recorded for the watcher to ignore.
	/// Must be called under IdeFileSystemLock.
	public async Task UpdateProjectPathsAfterMove(string oldPath, string newPath, CancellationToken cancellationToken = default)
	{
		if (_vsSolution is null || _solutionSerializer is null || _solutionFilePath is null) return;
		var solutionDirectory = Path.GetDirectoryName(_solutionFilePath)!;
		var oldFolderPrefix = Path.TrimEndingDirectorySeparator(oldPath) + Path.DirectorySeparatorChar;
		var updatedAny = false;
		foreach (var vsProject in _vsSolution.SolutionProjects)
		{
			var projectFullPath = Path.GetFullPath(Path.Combine(solutionDirectory, vsProject.FilePath));
			var newProjectFullPath = FilePathNormalizer.AreFilePathsEquivalent(projectFullPath, oldPath) ? newPath
				: projectFullPath.StartsWith(oldFolderPrefix, StringComparison.OrdinalIgnoreCase) ? Path.Combine(newPath, projectFullPath[oldFolderPrefix.Length..])
				: null;
			if (newProjectFullPath is null) continue;
			// Relative to the sln file directory, as in AddProject
			vsProject.FilePath = Path.GetRelativePath(solutionDirectory, newProjectFullPath).Replace('\\', '/');
			updatedAny = true;
		}
		if (updatedAny is false) return;
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
		if (_sharpIdeSolutionModel?.AllFiles.GetValueOrDefault(_solutionFilePath) is { } solutionFile) solutionFile.DiskStamp = FileStamp.TryRead(_solutionFilePath);
	}

	// Same as AddProject - we rely on the sln change from disk to update the SharpIdeSolutionModel
	public async Task AddSolutionFolder(ISolutionOrSolutionFolder parentNode, string solutionFolderName, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.Null(parentNode);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);
		Guard.Against.NullOrWhiteSpace(solutionFolderName);

		// VS Persistence solution folder paths start and end with a forward slash, e.g. /Parent/Child/
		var parentPath = parentNode is SharpIdeSolutionFolder solutionFolder ? solutionFolder.VsPersistencePath : "/";
		_vsSolution.AddFolder($"{parentPath}{solutionFolderName}/");
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
	}
}

public enum GitFileStatus
{
	Unaltered,
	Modified,
	Added
}

public enum GitLineStatus
{
	Unaltered,
	Modified,
	Added,
	Removed
}
