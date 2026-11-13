using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using PolyType.SourceGenerator;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Build;

/// Starts SharpIDE.MsBuildHost for a solution - with the SDK resolved from the solution's folder (global.json) - and connects JSON-RPC to it over stdin/stdout
internal static class MsBuildHostProcess
{
	/// designTime starts the design-time build host (serving IRpcDesignTimeBuildService) rather than the build host (IRpcBuildService)
	public static async Task<(Process Process, JsonRpc Rpc)> StartAsync(string solutionOrProjectFilePath, bool designTime, ILogger logger, Action<JsonRpc, JsonRpcDisconnectedEventArgs> onDisconnected)
	{
		var sharpIdeMsBuildHostDllPath = Path.Combine(AppContext.BaseDirectory, "SharpIdeMsBuildHost", "SharpIDE.MsBuildHost.dll");
		var slnOrProjectDirectory = Path.GetDirectoryName(solutionOrProjectFilePath) ?? throw new InvalidOperationException("Could not determine working directory from solution or project file path.");
		var sdkVersion = await GetCorrectDotnetSdkVersion(slnOrProjectDirectory);
		var startupInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			ArgumentList = { sharpIdeMsBuildHostDllPath, sdkVersion.ToNormalizedString(), slnOrProjectDirectory },
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		if (designTime) startupInfo.ArgumentList.Add("--design-time");
		var keysToRemove = startupInfo.Environment
			.Where(s => s.Key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || s.Key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase))
			.Where(s => s.Key.Equals("DOTNET_CLI_TELEMETRY_OPTOUT", StringComparison.OrdinalIgnoreCase) is false) // I don't know if this affects anything in MSBuild, but let's propagate it anyway if the user has it set
			.Select(s => s.Key)
			.ToList();
		keysToRemove.ForEach(s => startupInfo.Environment.Remove(s));
		startupInfo.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
		// ["DOTNET_ROLL_FORWARD"] = "LatestMajor" isn't necessary as it is set in the SharpIDE.MsBuildHost.runtimeconfig.json, from the <RollForward>LatestMajor</RollForward> csproj property
		var process = Process.Start(startupInfo);
		if (process is null) throw new InvalidOperationException("Failed to start SharpIDE.MsBuildHost");
		// stdout is the RPC channel. Drain stderr, so the host can never block on a full stderr buffer, and so a crash's output is logged.
		process.ErrorDataReceived += (_, e) =>
		{
			if (string.IsNullOrEmpty(e.Data) is false) logger.LogWarning("SharpIDE.MsBuildHost{Role} stderr: {Line}", designTime ? " (design-time)" : "", e.Data);
		};
		process.BeginErrorReadLine();
		var handler = new LengthHeaderMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new NerdbankMessagePackFormatter { TypeShapeProvider = TypeShapeProvider_SharpIDE_MsBuildHost_Contracts.Default });
		var rpc = new JsonRpc(handler);
		rpc.Disconnected += (_, e) => onDisconnected(rpc, e);
		rpc.StartListening();
		return (process, rpc);
	}

	private static async Task<NuGetVersion> GetCorrectDotnetSdkVersion(string slnOrProjectDirectory)
	{
		var result = await Cli.Wrap("dotnet").WithWorkingDirectory(slnOrProjectDirectory).WithArguments("--version").ExecuteBufferedAsync();
		return NuGetVersion.Parse(result.StandardOutput.Trim());
	}
}
