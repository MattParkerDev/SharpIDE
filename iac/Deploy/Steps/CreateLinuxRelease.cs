using CliWrap.Buffered;
using ParallelPipelines.Application.Attributes;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

[DependsOnStep<RestoreAndBuildStep>]
[DependsOnStep<CreateMacosRelease>]
public class CreateLinuxRelease : IStep
{
	public async Task<BufferedCommandResult?[]?> RunStep(CancellationToken cancellationToken)
	{
		var godotPublishDirectory = await PipelineFileHelper.GitRootDirectory.GetDirectory("./artifacts/publish-godot");
		godotPublishDirectory.Create();
		var linuxPublishDirectory = await godotPublishDirectory.GetDirectory("./linux");
		linuxPublishDirectory.Create();

		var godotProjectFile = await PipelineFileHelper.GitRootDirectory.GetFile("./src/SharpIDE.Godot/project.godot");

		var godotExportResult = await PipelineCliHelper.RunCliCommandAsync(
			"godot",
			$"--headless --verbose --export-release Linux --project {godotProjectFile.GetFullNameUnix()}",
			cancellationToken
		);

		var linuxTarballFile = await linuxPublishDirectory.TarballDirectoryToFile($"{PipelineFileHelper.GitRootDirectory.FullName}/artifacts/publish-godot/sharpide-linux-x64.tar.gz");

		return [godotExportResult];
	}
}
