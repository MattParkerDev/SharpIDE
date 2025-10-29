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
		using var repo = new Repository(solutionModel.DirectoryPath);
		var status = repo.RetrieveStatus(new StatusOptions());

		foreach (var entry in status.Where(s => s.State is not FileStatus.Ignored))
		{
			// Assumes solution file is at git repo root
			var filePath = new FileInfo(Path.Combine(solutionModel.DirectoryPath, entry.FilePath)).FullName; // used to normalise path separators
			var fileInSolution = solutionModel.AllFiles.SingleOrDefault(f => f.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));
			if (fileInSolution is null) continue;

			var mappedGitStatus = entry.State switch
			{
				FileStatus.NewInIndex | FileStatus.ModifiedInWorkdir => GitStatus.Added, // I've seen these appear together
				FileStatus.NewInIndex or FileStatus.NewInWorkdir => GitStatus.Added,
				FileStatus.ModifiedInIndex or FileStatus.ModifiedInWorkdir => GitStatus.Modified,
				_ => GitStatus.Unaltered // TODO: handle other kinds?
			};

			fileInSolution.GitStatus = mappedGitStatus;
		}

		return solutionModel;
	}
}

public enum GitStatus
{
	Unaltered,
	Modified,
	Added
}
