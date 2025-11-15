using CliWrap.Buffered;
using ParallelPipelines.Application.Attributes;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

[DependsOnStep<RestoreAndBuildStep>]
public class CreateWindowsRelease : IStep
{
	public async Task<BufferedCommandResult?[]?> RunStep(CancellationToken cancellationToken)
	{
		var godotPublishDirectory = await PipelineFileHelper.GitRootDirectory.GetDirectory("./artifacts/publish-godot");
		godotPublishDirectory.Create();
		var windowsPublishDirectory = await godotPublishDirectory.GetDirectory("./win");
		windowsPublishDirectory.Create();

		var godotProjectFile = await PipelineFileHelper.GitRootDirectory.GetFile("./src/SharpIDE.Godot/project.godot");

		var godotExportResult = await PipelineCliHelper.RunCliCommandAsync(
			"godot",
			$"--headless --verbose --export-release Windows --project {godotProjectFile.GetFullNameUnix()}",
			cancellationToken
		);

		var windowsZipFile = await windowsPublishDirectory.ZipDirectoryToFile($"{PipelineFileHelper.GitRootDirectory.FullName}/artifacts/publish-godot/sharpide-win-x64.zip");

		return [godotExportResult];
	}
}
