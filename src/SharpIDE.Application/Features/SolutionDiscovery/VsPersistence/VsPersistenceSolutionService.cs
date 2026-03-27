using Ardalis.GuardClauses;
using LibGit2Sharp;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public class VsPersistenceSolutionService
{
	private SolutionModel? _vsSolution;
	public async Task<SharpIdeSolutionModel> LoadSolution(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity();

		using (SharpIdeOtel.Source.StartActivity("VsPersistence.OpenSolution"))
		{
			var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
			Guard.Against.Null(serializer);
			_vsSolution = await serializer.OpenAsync(solutionFilePath, cancellationToken);
		}

		// This intermediate model is pretty much useless, but I have left it around as we grab the project nodes with it, which we might use later.
		var intermediateModel = await IntermediateMapper.GetIntermediateModel(solutionFilePath, _vsSolution, cancellationToken);

		var solutionModel = new SharpIdeSolutionModel(solutionFilePath, intermediateModel);

		var gitFolderPath = Repository.Discover(solutionFilePath);
		if (gitFolderPath is null) return solutionModel;
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

		return solutionModel;
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
