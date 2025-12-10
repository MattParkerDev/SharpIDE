using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using NuGet.Versioning;
using Octokit;
using ParallelPipelines.Application.Attributes;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

[DependsOnStep<CreateWindowsRelease>]
[DependsOnStep<CreateWindowsArm64Release>]
[DependsOnStep<CreateLinuxRelease>]
[DependsOnStep<CreateMacosRelease>]
public class CreateGithubRelease(IPipelineContext pipelineContext) : IStep
{
	public async Task<BufferedCommandResult?[]?> RunStep(CancellationToken cancellationToken)
	{
		var github = new GitHubClient(new ProductHeaderValue("SharpIDE-CI"));
		var token = pipelineContext.Configuration.GetValue<string>("GITHUB_TOKEN");
		var credentials = new Credentials(token);
		github.Credentials = credentials;

		var versionFile = await PipelineFileHelper.GitRootDirectory.GetFile("./src/SharpIDE.Godot/version.txt");
		if (versionFile.Exists is false) throw new FileNotFoundException(versionFile.FullName);
		var versionText = await File.ReadAllTextAsync(versionFile.FullName, cancellationToken);

		var version = NuGetVersion.Parse(versionText);
		var versionString = version.ToNormalizedString();
		var releaseTag = $"v{versionString}";

		var newRelease = new NewRelease(releaseTag)
		{
			Name = releaseTag,
			Body = "",
			Draft = true,
			Prerelease = false,
			GenerateReleaseNotes = true
		};
		var owner = "MattParkerDev";
		var repo = "SharpIDE";
		var release = await github.Repository.Release.Create(owner, repo, newRelease);

		var win64Release =		await UploadAssetToRelease(github, release, "./artifacts/publish-godot/sharpide-win-x64.zip", $"sharpide-win-x64-{versionString}.zip", cancellationToken);
		var winArm64Release =	await UploadAssetToRelease(github, release, "./artifacts/publish-godot/sharpide-win-arm64.zip", $"sharpide-win-arm64-{versionString}.zip", cancellationToken);
		var linuxRelease =		await UploadAssetToRelease(github, release, "./artifacts/publish-godot/sharpide-linux-x64.tar.gz", $"sharpide-linux-x64-{versionString}.tar.gz", cancellationToken);
		var macosRelease =		await UploadAssetToRelease(github, release, "./artifacts/publish-godot/sharpide-osx-universal.zip", $"sharpide-osx-universal-{versionString}.zip", cancellationToken);

		return null;
	}

	private static async Task<ReleaseAsset> UploadAssetToRelease(GitHubClient github, Release release, string relativeFilePath, string releaseFileName, CancellationToken cancellationToken)
	{
		var releaseArchive = await PipelineFileHelper.GitRootDirectory.GetFile(relativeFilePath);
		await using var releaseZipStream = releaseArchive.OpenRead();

		var upload = new ReleaseAssetUpload
		{
			FileName = releaseFileName,
			ContentType = "application/octet-stream",
			RawData = releaseZipStream
		};
		var asset = await github.Repository.Release.UploadAsset(release, upload, cancellationToken);
		return asset;
	}
}
