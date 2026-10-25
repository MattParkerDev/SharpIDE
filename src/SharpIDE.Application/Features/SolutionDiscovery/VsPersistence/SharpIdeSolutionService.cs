using Ardalis.GuardClauses;
using LibGit2Sharp;
using Microsoft.CodeAnalysis.Razor.Utilities;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.FileSystem;

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
		var (newSharpIdeSolutionModel, solutionModel, _) = await ReadSolution(_solutionFilePath, _sharpIdeSolutionModel!.RootFolder, cancellationToken);
		_vsSolution = solutionModel;
		_sharpIdeSolutionModel!.UpdateFromNewSolution(newSharpIdeSolutionModel);
		await _roslynAnalysis.ReloadSolution(cancellationToken);
		await _roslynAnalysis.UpdateSolutionDiagnostics(cancellationToken);
	}

	// Weird separation between ReadSolution and LoadSolution is so we can call the static ReadSolution in IdeRoot before all the UI Nodes are ready and DI services injected
	public static async Task<(SharpIdeSolutionModel, SolutionModel, ISolutionSerializer)> ReadSolution(string solutionFilePath, SharpIdeRootFolder sharpIdeRootFolder, CancellationToken cancellationToken = default)
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

		// This intermediate model is pretty much useless, but I have left it around as we grab the project nodes with it, which we might use later.
		var intermediateModel = await IntermediateMapper.GetIntermediateModel(solutionFilePath, vsSolution, cancellationToken);

		var solutionModel = new SharpIdeSolutionModel(solutionFilePath, intermediateModel, sharpIdeRootFolder);

		var gitFolderPath = Repository.Discover(solutionFilePath);
		if (gitFolderPath is null) return (solutionModel, vsSolution, solutionSerializer);
		using var repo = new Repository(gitFolderPath);
		var status = repo.RetrieveStatus(new StatusOptions());

		foreach (var entry in status.Where(s => s.State is not FileStatus.Ignored))
		{
			// Assumes solution file is at git repo root
			var filePath = new FileInfo(Path.Combine(solutionModel.DirectoryPath, entry.FilePath)).FullName; // used to normalise path separators
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

		return (solutionModel, vsSolution, solutionSerializer);
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
	public async Task RemoveSolutionFile(SharpIdeSolutionFile solutionFile, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(_vsSolution);
		Guard.Against.Null(_solutionSerializer);
		Guard.Against.Null(solutionFile);
		Guard.Against.NullOrWhiteSpace(_solutionFilePath);
		if (solutionFile.Parent is not SharpIdeSolutionFolder solutionFolder) throw new InvalidOperationException("Solution files must be in a solution folder");

		var vsSolutionFolder = _vsSolution.FindFolder(solutionFolder.VsPersistencePath);
		Guard.Against.Null(vsSolutionFolder);
		var solutionDirectory = Path.GetDirectoryName(_solutionFilePath)!;
		var vsFile = vsSolutionFolder.Files?.SingleOrDefault(f => FilePathNormalizer.AreFilePathsEquivalent(Path.GetFullPath(Path.Combine(solutionDirectory, f)), solutionFile.Path));
		Guard.Against.Null(vsFile);

		vsSolutionFolder.RemoveFile(vsFile);
		await _solutionSerializer.SaveAsync(_solutionFilePath, _vsSolution, cancellationToken);
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
