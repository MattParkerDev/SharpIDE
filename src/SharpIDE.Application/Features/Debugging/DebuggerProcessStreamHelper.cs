using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Run;

namespace SharpIDE.Application.Features.Debugging;

public static class DebuggerProcessStreamHelper
{
	public static (Stream Input, Stream Output, bool IsNetCoreDbg, IDisposable DebuggerDisposable) NewDebuggerProcessStreamsForInfo(DebuggerExecutableInfo? debuggerExecutableInfoNullable, ILogger<DebuggingService> logger)
	{
		if (debuggerExecutableInfoNullable is not { } debuggerExecutableInfo) throw new ArgumentNullException(nameof(debuggerExecutableInfoNullable), "Debugger executable info cannot be null.");
		if (debuggerExecutableInfo.UseSharpDbg) return StartBundledSharpDbg(logger);
		var debuggerExecutablePath = debuggerExecutableInfo.DebuggerExecutablePath;
		Guard.Against.NullOrWhiteSpace(debuggerExecutablePath, "Debugger executable path cannot be null or empty.");
		var isNetCoreDbg = Path.GetFileNameWithoutExtension(debuggerExecutablePath).Equals("netcoredbg", StringComparison.OrdinalIgnoreCase);

		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				//FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
				FileName = debuggerExecutablePath,
				Arguments = "--interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		process.Start();
		return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, isNetCoreDbg, process);
	}

	/// SharpDbg.Cli is copied into the SharpDbg folder from the SharpDbg.Cli tool package - see SharpIDE.Application.csproj
	private static (Stream Input, Stream Output, bool IsNetCoreDbg, IDisposable DebuggerDisposable) StartBundledSharpDbg(ILogger logger)
	{
		var sharpDbgDllPath = Path.Combine(AppContext.BaseDirectory, "SharpDbg", "SharpDbg.Cli.dll");
		if (File.Exists(sharpDbgDllPath) is false) throw new FileNotFoundException("The bundled SharpDbg debugger was not found", sharpDbgDllPath);
		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			ArgumentList = { sharpDbgDllPath, "--interpreter=vscode" },
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		// SharpDbg.Cli rolls forward to the latest major runtime (RollForward=Major); allow that to be a preview runtime, as for SharpIDE.MsBuildHost
		startInfo.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
		var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start SharpDbg");
		// stdout is the DAP channel. Drain stderr, so SharpDbg can never block on a full stderr buffer, and so a crash's output is logged.
		process.ErrorDataReceived += (_, e) =>
		{
			if (string.IsNullOrEmpty(e.Data) is false) logger.LogWarning("SharpDbg stderr: {Line}", e.Data);
		};
		process.BeginErrorReadLine();
		process.EnableRaisingEvents = true;
		process.Exited += (_, _) => logger.LogInformation("SharpDbg exited with code {ExitCode}", process.ExitCode);
		return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, false, process);
	}
}
