using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using NuGet.Versioning;
using Octokit;
using ParallelPipelines.Application.Attributes;
using ParallelPipelines.Domain.Entities;
using ParallelPipelines.Host.Helpers;

namespace Deploy.Steps;

[DependsOnStep<CreateWindowsRelease>]
public class CreateGithubRelease(IPipelineContext pipelineContext) : IStep
{
	public async Task<BufferedCommandResult?[]?> RunStep(CancellationToken cancellationToken)
	{
		var github = new GitHubClient(new ProductHeaderValue("SharpIDE-CI"));
		var token = pipelineContext.Configuration.GetValue<string>("GITHUB_TOKEN");
		var credentials = new Credentials(token);
		github.Credentials = credentials;

		var version = NuGetVersion.Parse("0.1.1");
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

		var windowsReleaseZip = await PipelineFileHelper.GitRootDirectory.GetFile("./artifacts/publish-godot/sharpide-win-x64.zip");
		await using var stream = windowsReleaseZip.OpenRead();
		var upload = new ReleaseAssetUpload
		{
			FileName = $"sharpide-win-x64-{versionString}.zip",
			ContentType = "application/octet-stream",
			RawData = stream
		};
		var asset = await github.Repository.Release.UploadAsset(release, upload, cancellationToken);
		return null;
	}
}
