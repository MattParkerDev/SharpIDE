using System.Diagnostics;
using Microsoft.Build.Locator;

namespace SharpIDE.MsBuildHost;

public static class SharpIdeMsbuildLocator
{
	public static string ResolvedMsBuildSdkPath { get; private set; } = null!;
	public static async Task Register(string sdkVersion, string projectOrSlnDirectory)
	{
		if (OperatingSystem.IsMacOS())
		{
			await FixMacosPath();
		}
		Environment.SetEnvironmentVariable("MSBUILD_PARSE_SLN_WITH_SOLUTIONPERSISTENCE", "1");
		// Using VisualStudioInstanceQueryOptions with WorkingDirectory set doesn't seem to resolve a local SDK, but having this process's current directory set does
		var originalWorkingDirectory = Environment.CurrentDirectory;
		Environment.CurrentDirectory = projectOrSlnDirectory;
		// Use latest version - https://github.com/microsoft/MSBuildLocator/issues/81
		var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
		var instance = instances.FirstOrDefault(s => s.MSBuildPath.EndsWith(sdkVersion, StringComparison.OrdinalIgnoreCase));
		if (instance is null)
		{
			var found = instances.Count is 0 ? "none" : string.Join(", ", instances.Select(s => $"{s.Version} ({s.MSBuildPath})"));
			throw new InvalidOperationException($"MSBuild for .NET SDK {sdkVersion} was not found. The build host is running on .NET {Environment.Version} from '{Environment.ProcessPath}', where MSBuildLocator found these SDKs: {found}.");
		}
		MSBuildLocator.RegisterInstance(instance);
		Environment.CurrentDirectory = originalWorkingDirectory;
		ResolvedMsBuildSdkPath = instance.MSBuildPath;
	}

	// https://github.com/microsoft/MSBuildLocator/issues/361
	private static async Task FixMacosPath()
	{
		// Drains stdout and stderr together, so a chatty login shell can't fill the stderr pipe and hang us
		var result = await Process.RunAndCaptureTextAsync("/bin/zsh", ["-l", "-c", "printenv PATH"]);
		var path = result.StandardOutput.Trim();
		if (string.IsNullOrEmpty(path)) return;
		Environment.SetEnvironmentVariable("PATH", path, EnvironmentVariableTarget.Process);
	}
}
