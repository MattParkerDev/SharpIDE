#:package Octokit
#:package NuGet.Versioning
using Octokit;
using NuGet.Versioning;

var versionFile = new FileInfo(Path.Combine(GetGitRootPath(), "src", "SharpIDE.Godot", "version.txt"));
if (versionFile.Exists is false) throw new FileNotFoundException(versionFile.FullName);
var versionText = await File.ReadAllTextAsync(versionFile.FullName);

var version = NuGetVersion.Parse(versionText);
var versionString = version.ToNormalizedString();
var releaseTag = $"v{versionString}";

var github = new GitHubClient(new ProductHeaderValue("SharpIDE-CI"));
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

static string GetGitRootPath()
{
	var currentDirectory = Directory.GetCurrentDirectory();
	var gitRoot = currentDirectory;
	while (!Directory.Exists(Path.Combine(gitRoot, ".git")))
	{
		gitRoot = Path.GetDirectoryName(gitRoot); // parent directory
		if (string.IsNullOrWhiteSpace(gitRoot))
		{
			throw new Exception("Could not find git root");
		}
	}

	return gitRoot;
}
