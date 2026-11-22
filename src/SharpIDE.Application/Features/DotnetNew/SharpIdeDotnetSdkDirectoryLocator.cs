using System.Diagnostics;
using NuGet.Versioning;
using SharpIDE.Application.Extensions;

namespace SharpIDE.Application.Features.DotnetNew;

public sealed record DotnetTemplateSdk(string Version, string Directory);

public static class SharpIdeDotnetSdkDirectoryLocator
{
	public static async Task<IReadOnlyList<DotnetTemplateSdk>> GetInstalledSdks(string workingDirectory, CancellationToken cancellationToken = default)
	{
		var result = await Process.RunAndCaptureTextInDirAsync("dotnet", ["--list-sdks"], workingDirectory, cancellationToken);
		return ParseInstalledSdks(result.StandardOutput);
	}

	public static IReadOnlyList<DotnetTemplateSdk> ParseInstalledSdks(string output)
	{
		var sdks = new List<DotnetTemplateSdk>();
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var bracket = line.IndexOf('[');
			if (bracket < 0 || !line.EndsWith(']')) continue;
			var version = line[..bracket].Trim();
			if (!NuGetVersion.TryParse(version, out _)) continue;
			sdks.Add(new DotnetTemplateSdk(version, Path.Combine(line[(bracket + 1)..^1], version)));
		}
		return sdks.OrderByDescending(sdk => NuGetVersion.Parse(sdk.Version)).ToArray();
	}

	public static async Task<DotnetTemplateSdk> ResolveSdk(string workingDirectory, IReadOnlyList<DotnetTemplateSdk> installedSdks, CancellationToken cancellationToken = default)
	{
		// Let dotnet implement global.json lookup, rollForward and allowPrerelease.
		var result = await Process.RunAndCaptureTextInDirAsync("dotnet", ["--version"], workingDirectory, cancellationToken);
		var version = result.StandardOutput.Trim();
		return installedSdks.FirstOrDefault(sdk => sdk.Version == version)
			?? throw new InvalidOperationException($"Resolved SDK {version} was not found in dotnet --list-sdks.");
	}
}
