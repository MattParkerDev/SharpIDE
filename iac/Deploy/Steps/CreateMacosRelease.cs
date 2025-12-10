using CliWrap.Buffered;
using ParallelPipelines.Application.Attributes;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

[DependsOnStep<RestoreAndBuildStep>]
[DependsOnStep<CreateWindowsArm64Release>]
public class CreateMacosRelease : IStep
{
	public async Task<BufferedCommandResult?[]?> RunStep(CancellationToken cancellationToken)
	{
		var godotPublishDirectory = await PipelineFileHelper.GitRootDirectory.GetDirectory("./artifacts/publish-godot");
		godotPublishDirectory.Create();
		var macosPublishDirectory = await godotPublishDirectory.GetDirectory("./osx");
		macosPublishDirectory.Create();

		var godotProjectFile = await PipelineFileHelper.GitRootDirectory.GetFile("./src/SharpIDE.Godot/project.godot");

		var godotExportResult = await PipelineCliHelper.RunCliCommandAsync(
			"godot",
			$"--headless --verbose --export-release macOS --project {godotProjectFile.GetFullNameUnix()}",
			cancellationToken
		);

		var macosZippedAppFile = await macosPublishDirectory.GetFile("SharpIDE.zip");
		macosZippedAppFile.MoveTo($"{PipelineFileHelper.GitRootDirectory.FullName}/artifacts/publish-godot/sharpide-osx-universal.zip");

		return [godotExportResult];
	}
}
