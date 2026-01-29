using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.Extensions.Logging;
using SharpDbg.InMemory;
using SharpIDE.Application.Features.Run;

namespace SharpIDE.Application.Features.Debugging;

public static class DebuggerProcessStreamHelper
{
	public static (Stream Input, Stream Output, bool IsNetCoreDbg) NewDebuggerProcessStreamsForInfo(DebuggerExecutableInfo? debuggerExecutableInfoNullable, ILogger<DebuggingService> logger)
	{
		if (debuggerExecutableInfoNullable is not {} debuggerExecutableInfo) throw new ArgumentNullException(nameof(debuggerExecutableInfoNullable), "Debugger executable info cannot be null.");
		if (debuggerExecutableInfo.UseInMemorySharpDbg)
		{
			var (input, output) = SharpDbgInMemory.NewDebugAdapterStreams(s =>
			{
				logger.LogInformation("SharpDbgInMemory: {Message}", s);
			});
			return (input, output, false);
		}
		var debuggerExecutablePath = debuggerExecutableInfo.DebuggerExecutablePath;
		Guard.Against.NullOrWhiteSpace(debuggerExecutablePath, nameof(debuggerExecutablePath), "Debugger executable path cannot be null or empty.");
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
		return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, isNetCoreDbg);
	}
}
