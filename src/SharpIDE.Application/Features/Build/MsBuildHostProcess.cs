using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using PolyType.SourceGenerator;
using SharpIDE.Application.Extensions;
using SharpIDE.Application.Features.Run;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Build;

/// Starts SharpIDE.MsBuildHost for a solution - with the SDK resolved from the solution's folder (global.json) - and connects JSON-RPC to it over stdin/stdout
internal static class MsBuildHostProcess
{
	private const string RuntimeRequirement = "SharpIDE's build host requires the .NET 11 runtime (included with the .NET SDK 11) - install it from https://dotnet.microsoft.com/download, and make sure it is in the first 'dotnet' on SharpIDE's PATH (when SharpIDE is started from an app menu or launcher, PATH changes made in shell profiles may only apply after logging out and back in).";
	/// hostfxr's "framework not found" exit code - 0x80008096 on Windows, truncated to its low byte (0x96) on Unix
	private const int FrameworkMissingExitCode = unchecked((int)0x80008096);
	private const int FrameworkMissingExitCodeUnix = 0x96;
	private const int MaxStartupStderrLines = 100;

	/// designTime starts the design-time build host (serving IRpcDesignTimeBuildService) rather than the build host (IRpcBuildService)
	public static async Task<(Process Process, JsonRpc Rpc, T HandshakeResult)> StartAsync<T>(string solutionOrProjectFilePath, bool designTime, ILogger logger, Func<JsonRpc, Task<T>> handshake, Action<JsonRpc, JsonRpcDisconnectedEventArgs> onDisconnected)
	{
		var sharpIdeMsBuildHostDllPath = Path.Combine(AppContext.BaseDirectory, "SharpIdeMsBuildHost", "SharpIDE.MsBuildHost.dll");
		var slnOrProjectDirectory = Path.GetDirectoryName(solutionOrProjectFilePath) ?? throw new InvalidOperationException("Could not determine working directory from solution or project file path.");
		var dotnetDescription = DescribeDotnet(FindDotnetOnPath());
		var sdkVersion = await GetSdkVersion(slnOrProjectDirectory, dotnetDescription);
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
		ChildProcessEnvironment.ConfigureForSharpIdeHelperProcess(startupInfo, removeMsBuildVariables: true);
		// ["DOTNET_ROLL_FORWARD"] = "LatestMajor" isn't necessary as it is set in the SharpIDE.MsBuildHost.runtimeconfig.json, from the <RollForward>LatestMajor</RollForward> csproj property
		var process = Process.Start(startupInfo);
		if (process is null) throw new InvalidOperationException("Failed to start SharpIDE.MsBuildHost");
		var startupStderr = new ConcurrentQueue<string>();
		var handshakeCompleted = false;
		// stdout is the RPC channel. Drain stderr, so the host can never block on a full stderr buffer, and so a crash's output is logged.
		process.ErrorDataReceived += (_, e) =>
		{
			if (string.IsNullOrEmpty(e.Data)) return;
			logger.LogWarning("SharpIDE.MsBuildHost{Role} stderr: {Line}", designTime ? " (design-time)" : "", e.Data);
			if (Volatile.Read(ref handshakeCompleted) is false && startupStderr.Count < MaxStartupStderrLines) startupStderr.Enqueue(e.Data);
		};
		process.BeginErrorReadLine();
		var handler = new LengthHeaderMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new NerdbankMessagePackFormatter { TypeShapeProvider = TypeShapeProvider_SharpIDE_MsBuildHost_Contracts.Default });
		var rpc = new JsonRpc(handler);
		rpc.Disconnected += (_, e) => onDisconnected(rpc, e);
		rpc.StartListening();
		try
		{
			var handshakeResult = await handshake(rpc);
			Volatile.Write(ref handshakeCompleted, true);
			startupStderr.Clear();
			return (process, rpc, handshakeResult);
		}
		catch (Exception ex)
		{
			using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try { await process.WaitForExitAsync(exitTimeout.Token); } catch (OperationCanceledException) { }
			int? exitCode = process.HasExited ? process.ExitCode : null;
			rpc.Dispose();
			try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // Already exited
			process.Dispose();
			var stderr = string.Join(' ', startupStderr).Trim();
			throw new MsBuildHostStartException(DescribeStartupFailure(exitCode, stderr, sdkVersion, dotnetDescription, ex), ex);
		}
	}

	private static string DescribeStartupFailure(int? exitCode, string stderr, NuGetVersion sdkVersion, string dotnetDescription, Exception handshakeException)
	{
		if (exitCode is FrameworkMissingExitCode or FrameworkMissingExitCodeUnix || stderr.Contains("You must install or update .NET", StringComparison.Ordinal))
		{
			return $"SharpIDE's build host could not start, as the .NET 11 runtime was not found for {dotnetDescription}. {RuntimeRequirement}";
		}
		if (exitCode is not null)
		{
			var output = stderr.Length is 0 ? "" : $": {Truncate(stderr, 500)}";
			return $"SharpIDE's build host exited during startup with exit code {exitCode} (using {dotnetDescription}, .NET SDK {sdkVersion}){output}";
		}
		return $"SharpIDE's build host did not respond during startup (using {dotnetDescription}, .NET SDK {sdkVersion}): {handshakeException.Message}";
	}

	private static async Task<NuGetVersion> GetSdkVersion(string slnOrProjectDirectory, string dotnetDescription)
	{
		ProcessTextOutput result;
		try
		{
			result = await Process.RunAndCaptureTextInDirAsync("dotnet", ["--version"], slnOrProjectDirectory);
		}
		catch (Win32Exception ex)
		{
			throw new MsBuildHostStartException($"No .NET SDK was found - 'dotnet' is not on SharpIDE's PATH. {RuntimeRequirement}", ex);
		}
		var output = result.StandardOutput.Trim();
		if (result.ExitStatus.ExitCode is not 0 || NuGetVersion.TryParse(output, out var sdkVersion) is false)
		{
			throw new MsBuildHostStartException($"{dotnetDescription} could not resolve a .NET SDK for '{slnOrProjectDirectory}': {DescribeSdkResolutionFailure(result.StandardError)}");
		}
		return sdkVersion;
	}

	private static string DescribeSdkResolutionFailure(string standardError)
	{
		string[] relevantLinePrefixes = ["A compatible .NET SDK was not found", "Requested SDK version:", "global.json file:"];
		var lines = standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var relevantLines = lines.Where(line => relevantLinePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))).ToList();
		return relevantLines.Count > 0 ? string.Join(' ', relevantLines) : Truncate(string.Join(' ', lines), 500);
	}

	private static string? FindDotnetOnPath()
	{
		var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var candidate = Path.Combine(directory.Trim('"'), fileName);
			if (File.Exists(candidate) is false) continue;
			try
			{
				return File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? candidate;
			}
			catch (IOException)
			{
				return candidate;
			}
		}
		return null;
	}

	private static string DescribeDotnet(string? dotnetPath) => dotnetPath is null ? "'dotnet'" : $"'{dotnetPath}'";

	private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : $"{text[..maxLength]}...";
}
