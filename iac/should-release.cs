#:package Octokit
#:package NuGet.Versioning
using Octokit;
using NuGet.Versioning;

var github = new GitHubClient(new ProductHeaderValue("SharpIDE-CI"));
var version = NuGetVersion.Parse("0.1.2");
var versionString = version.ToNormalizedString();
var releaseTag = $"v{versionString}";
var owner = "MattParkerDev";
var repo = "SharpIDE";
var release = await GetReleaseOrNull();

var resultString = release is null ? "true" : "false";
Console.WriteLine(resultString);
return 0;

async Task<Release?> GetReleaseOrNull()
{
	try
	{
		var release = await github.Repository.Release.Get(owner, repo, releaseTag);
		return release;
	}
	catch (NotFoundException)
	{
		return null;
	}
}
