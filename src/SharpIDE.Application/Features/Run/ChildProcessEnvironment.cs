using System.Diagnostics;

namespace SharpIDE.Application.Features.Run;

public static class ChildProcessEnvironment
{
	/// Set on SharpIDE by whatever launched it (Rider/VS hot reload, or a debugger e.g. SharpIDE debugging SharpIDE), and meant only for SharpIDE itself.
	/// No process SharpIDE starts should inherit them - e.g. DOTNET_STARTUP_HOOKS loads Rider's hot reload agent, which then waits ~5 seconds trying to connect to Rider.
	private static readonly string[] LauncherInjectedVariables =
	[
		"DOTNET_STARTUP_HOOKS",
		"DOTNET_MODIFIABLE_ASSEMBLIES",
		"DOTNET_WATCH_HOTRELOAD_NAMEDPIPE_NAME",
		"DOTNET_HOTRELOAD_NAMEDPIPE_NAME",
		"DOTNET_DefaultDiagnosticPortSuspend",
	];

	/// Removes the variables SharpIDE's launcher set from SharpIDE's own environment, so that no child process inherits them - run/debug, tests, build hosts, debugger.
	/// Call once at startup, before any process is started. The runtime has already read them by then, so this doesn't affect SharpIDE itself.
	public static void RemoveLauncherInjectedVariablesFromCurrentProcess()
	{
		foreach (var variable in LauncherInjectedVariables)
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	/// For SharpIDE's own .NET helper processes (SharpIDE.MsBuildHost, SharpDbg) - they must not inherit any runtime configuration aimed at SharpIDE or the user's apps.
	/// Unlike the user's own processes, which keep any DOTNET_* variables the user has set (e.g. DOTNET_ENVIRONMENT).
	public static void ConfigureForSharpIdeHelperProcess(ProcessStartInfo startInfo, bool removeMsBuildVariables = false)
	{
		var keysToRemove = startInfo.Environment
			.AsValueEnumerable()
			.Where(s => s.Key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || (removeMsBuildVariables && s.Key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)))
			.Where(s => s.Key.Equals("DOTNET_CLI_TELEMETRY_OPTOUT", StringComparison.OrdinalIgnoreCase) is false) // Respect the user's telemetry opt-out
			.Select(s => s.Key)
			.ToList();
		foreach (var key in keysToRemove) startInfo.Environment.Remove(key);
		// Helper processes roll forward to the latest major runtime (RollForward in their runtimeconfig.json); allow that to be a preview runtime
		startInfo.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
	}
}
