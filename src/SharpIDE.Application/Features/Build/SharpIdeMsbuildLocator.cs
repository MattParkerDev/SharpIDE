using System.Diagnostics;
using Microsoft.Build.Locator;

namespace SharpIDE.Application.Features.Build;

public static class SharpIdeMsbuildLocator
{
	public static void Register()
	{
		if (OperatingSystem.IsMacOS())
		{
			FixMacosPath();
		}
		// Use latest version - https://github.com/microsoft/MSBuildLocator/issues/81
		var instance = MSBuildLocator.QueryVisualStudioInstances().MaxBy(s => s.Version);
		if (instance is null) throw new InvalidOperationException("No MSBuild instances found");
		MSBuildLocator.RegisterInstance(instance);
	}

	// https://github.com/microsoft/MSBuildLocator/issues/361
	private static void FixMacosPath()
	{
		var processStartInfo = new ProcessStartInfo
		{
			FileName = "/bin/zsh",
			ArgumentList = { "-l", "-c", "printenv PATH" },
			RedirectStandardOutput = true,
			RedirectStandardError =  true,
			UseShellExecute = false,
		};
		using var process = Process.Start(processStartInfo);
		var output = process!.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();
		Environment.SetEnvironmentVariable("PATH", output, EnvironmentVariableTarget.Process);
	}
}
