using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using NuGet.Versioning;
using Octokit;
using ParallelPipelines.Application.Attributes;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

[DependsOnStep<CreateWindowsRelease>]
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

		//var owner = "MattParkerDev";
		//var repo = "SharpIDE";

		var owner = "talynone";
		var repo = "SharpIDE";

		var release = await github.Repository.Release.Create(owner, repo, newRelease);

		var windowsReleaseZip = await PipelineFileHelper.GitRootDirectory.GetFile("./artifacts/publish-godot/sharpide-win-x64.zip");
		await using var stream = windowsReleaseZip.OpenRead();
		var upload = new ReleaseAssetUpload
		{
			FileName = $"sharpide-win-x64-{versionString}.zip",
			ContentType = "application/octet-stream",
			RawData = stream
		};
		var asset = await github.Repository.Release.UploadAsset(release, upload, cancellationToken);

		// Add Windows ARM64 release asset
		var winArm64ReleaseZip = await PipelineFileHelper.GitRootDirectory.GetFile("./artifacts/publish-godot/sharpide-win-arm64.zip");
		await using var winArm64Stream = winArm64ReleaseZip.OpenRead();
		var winArm64Upload = new ReleaseAssetUpload
		{
			FileName = $"sharpide-win-arm64-{versionString}.zip",
			ContentType = "application/octet-stream",
			RawData = winArm64Stream
		};
		var winArm64Asset = await github.Repository.Release.UploadAsset(release, winArm64Upload, cancellationToken);

		var linuxReleaseTarball = await PipelineFileHelper.GitRootDirectory.GetFile("./artifacts/publish-godot/sharpide-linux-x64.tar.gz");
		await using var linuxStream = linuxReleaseTarball.OpenRead();
		var linuxUpload = new ReleaseAssetUpload
		{
			FileName = $"sharpide-linux-x64-{versionString}.tar.gz",
			ContentType = "application/gzip",
			RawData = linuxStream
		};
		var linuxAsset = await github.Repository.Release.UploadAsset(release, linuxUpload, cancellationToken);

		var macosReleaseZip = await PipelineFileHelper.GitRootDirectory.GetFile("./artifacts/publish-godot/sharpide-osx-universal.zip");
		await using var macosStream = macosReleaseZip.OpenRead();
		var macosUpload = new ReleaseAssetUpload
		{
			FileName = $"sharpide-osx-universal-{versionString}.zip",
			ContentType = "application/octet-stream",
			RawData = macosStream
		};
		var macosAsset = await github.Repository.Release.UploadAsset(release, macosUpload, cancellationToken);

		return null;
	}
}
