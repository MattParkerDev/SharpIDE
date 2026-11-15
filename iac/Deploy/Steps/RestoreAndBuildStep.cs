using CliWrap.Buffered;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

public class RestoreAndBuildStep : IStep
{
	public async Task<BufferedCommandResult?[]?> RunStep(CancellationToken cancellationToken)
	{
		var slnFileName = DeploymentConstants.SolutionFileName;
		var slnFile = await PipelineFileHelper.GitRootDirectory.GetFile(slnFileName);

		var restoreResult = await PipelineCliHelper.RunCliCommandAsync(
			"dotnet",
			$"restore {slnFile.FullName}",
			cancellationToken
		);

		var buildResult = await PipelineCliHelper.RunCliCommandAsync(
			"dotnet",
			$"build {slnFile.FullName} --no-restore -c Release",
			cancellationToken
		);

		return [restoreResult, buildResult];
	}
}
