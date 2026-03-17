using System.Diagnostics;
using LibGit2Sharp;

namespace SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

public static class VsPersistenceMapper
{
	public static async Task<SharpIdeSolutionModel> GetSolutionModel(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity();
		// This intermediate model is pretty much useless, but I have left it around as we grab the project nodes with it, which we might use later.
		var intermediateModel = await IntermediateMapper.GetIntermediateModel(solutionFilePath, cancellationToken);

		var solutionModel = new SharpIdeSolutionModel(solutionFilePath, intermediateModel);

		var gitFolderPath = Repository.Discover(solutionFilePath);
		if (gitFolderPath is null) return solutionModel;
		using var repo = new Repository(gitFolderPath);
		var status = repo.RetrieveStatus(new StatusOptions());
		var repoRoot = repo.Info.WorkingDirectory;

		foreach (var entry in status.Where(s => s.State is not FileStatus.Ignored))
		{
			var filePath = new FileInfo(Path.Combine(repoRoot, entry.FilePath)).FullName; // used to normalise path separators
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
