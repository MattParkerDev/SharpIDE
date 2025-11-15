using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
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

		var newRelease = new NewRelease("v0.1.1")
		{
			Name = "SharpIDE v0.1.1",
			Body = "Automated release created by CI pipeline.",
			Draft = true,
			Prerelease = false
		};
		var owner = "MattParkerDev";
		var repo = "SharpIDE";
		var release = await github.Repository.Release.Create(owner, repo, newRelease);
		return null;
	}
}
