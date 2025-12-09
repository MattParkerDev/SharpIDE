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

		var godotProjectFile = await PipelineFileHelper.GitRootDirectory.GetFile("./src/SharpIDE.Godot/project.godot");

		var results = new List<BufferedCommandResult?>();

		// Windows x64
		var win64Dir = await godotPublishDirectory.GetDirectory("./win-x64");
		win64Dir.Create();
		var win64Export = await PipelineCliHelper.RunCliCommandAsync(
			"godot",
			$"--headless --verbose --export-release \"Windows\" --project {godotProjectFile.GetFullNameUnix()}",
			cancellationToken
		);
		var win64Zip = await win64Dir.ZipDirectoryToFile($"{godotPublishDirectory.FullName}/sharpide-win-x64.zip");
		results.Add(win64Export);

		// Windows ARM64
		var winArm64Dir = await godotPublishDirectory.GetDirectory("./win-arm64");
		winArm64Dir.Create();
		var winArm64Export = await PipelineCliHelper.RunCliCommandAsync(
			"godot",
			$"--headless --verbose --export-release \"Windows ARM64\" --project {godotProjectFile.GetFullNameUnix()}",
			cancellationToken
		);
		var winArm64Zip = await winArm64Dir.ZipDirectoryToFile($"{godotPublishDirectory.FullName}/sharpide-win-arm64.zip");
		results.Add(winArm64Export);

		return results.ToArray();
	}
}
