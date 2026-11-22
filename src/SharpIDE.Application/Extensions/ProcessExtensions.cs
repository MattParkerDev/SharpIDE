using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace SharpIDE.Application.Extensions;

public static class ProcessExtensions
{
	extension(Process process)
	{
		public static async Task<ProcessTextOutput> RunAndCaptureTextInDirAsync(string targetFilePath, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
		{
			var processStartInfo = new ProcessStartInfo(targetFilePath, arguments)
			{
				WorkingDirectory = workingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			return await Process.RunAndCaptureTextAsync(processStartInfo, cancellationToken);
		}

		public static async Task<ProcessExitStatus> Run2Async(string targetFilePath, IEnumerable<string> arguments, bool silent = false, CancellationToken cancellationToken = default)
		{
			using SafeFileHandle? nullHandle = silent ? File.OpenNullHandle() : null;
			var processStartInfo = new ProcessStartInfo(targetFilePath, arguments)
			{
				StandardInputHandle = nullHandle,
				StandardOutputHandle = nullHandle,
				StandardErrorHandle = nullHandle,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			var exitStatus = await Process.RunAsync(processStartInfo, cancellationToken);
			return exitStatus;
		}
	}
}
