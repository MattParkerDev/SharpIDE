#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

try
{
	var sharpIdeInstallPath = args.ElementAtOrDefault(0);
	var sharpIdeRunningExecutableFilePath = args.ElementAtOrDefault(1);
	var newSharpIdeReleaseFilePath = args.ElementAtOrDefault(2);
	var runningIdeInstancePidString = args.ElementAtOrDefault(3);

	var logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharpIDE", "update-log.txt");
	var logFileStream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
	var logStreamWriter = new StreamWriter(logFileStream) { AutoFlush = true };
	Console.SetOut(logStreamWriter);
	Console.WriteLine();

	if (string.IsNullOrWhiteSpace(runningIdeInstancePidString)) throw new InvalidOperationException("SharpIDE PID was not provided");
	var runningIdeInstancePid = int.Parse(runningIdeInstancePidString);
	var sharpIdeInstallDir = new DirectoryInfo(sharpIdeInstallPath!);
	if (sharpIdeInstallDir.Exists is false) throw new DirectoryNotFoundException($"SharpIDE install directory not found: '{sharpIdeInstallPath}'");
	if (File.Exists(sharpIdeRunningExecutableFilePath) is false) throw new FileNotFoundException("Running SharpIDE executable file not found", sharpIdeRunningExecutableFilePath);
	if (File.Exists(newSharpIdeReleaseFilePath) is false) throw new FileNotFoundException("New SharpIDE release file not found", newSharpIdeReleaseFilePath);

	Console.WriteLine($"Update will be installed at: {sharpIdeInstallPath}");
	Console.WriteLine($"Executable to run after update: {sharpIdeRunningExecutableFilePath}");
	Console.WriteLine($"New Release: {newSharpIdeReleaseFilePath}");
	Console.WriteLine($"Updater PID: {Environment.ProcessId}");

	try
	{
		// wait until the runningIdeInstancePid process ends
		var runningIdeProcess = Process.GetProcessById(runningIdeInstancePid);
		if (runningIdeProcess is null) throw new InvalidOperationException("Process not found");
		Console.WriteLine($"Waiting for SharpIDE process (PID: {runningIdeInstancePid}) to exit...");
		await runningIdeProcess.WaitForExitAsync();
	}
	catch (ArgumentException)
	{
		// The process already exited
	}

	Console.WriteLine($"SharpIDE process exited, proceeding with update...");
	var currentWorkingDirectory = Environment.CurrentDirectory;

	if (OperatingSystem.IsMacOS())
	{
		var sharpIdeAppDir = sharpIdeInstallDir;
		while (sharpIdeAppDir is not null && sharpIdeAppDir.Extension != ".app")
		{
			sharpIdeAppDir = sharpIdeAppDir.Parent;
		}
		if (sharpIdeAppDir is null || sharpIdeAppDir.Name is not "SharpIDE.app") throw new InvalidOperationException("Could not find SharpIDE.app directory containing SharpIDE install directory");
		Console.WriteLine($"MacOS: .app to be replaced: {sharpIdeAppDir.FullName}");
		Console.WriteLine("Removing old version...");
		var sharpIdeAppParentDirectory = sharpIdeAppDir.Parent ?? throw new InvalidOperationException("Could not find parent directory of SharpIDE.app");
		sharpIdeAppDir.Delete(true);

		Console.WriteLine("Copying new version...");
		using var archive = await ZipFile.OpenReadAsync(newSharpIdeReleaseFilePath);
		await archive.ExtractToDirectoryAsync(sharpIdeAppParentDirectory.FullName);

		Console.WriteLine("Macos: AdHoc code-signing...");
		// On MacOS, Process.Start will attempt to resolved the supplied working directory, by getting the cwd of this process
		// We technically deleted and replaced it, which means it blows up. Re-resolve the cwd.
		Environment.CurrentDirectory = currentWorkingDirectory;
		var psi = new ProcessStartInfo
		{
			FileName = "codesign",
			ArgumentList = { "--force", "--deep", "--sign", "-", sharpIdeAppDir.FullName },
			WorkingDirectory = currentWorkingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		var codeSignProcess = Process.Start(psi);
		await codeSignProcess.WaitForExitAsync();
	}
	else
	{
		var dirs = sharpIdeInstallDir.EnumerateDirectories().ToList();

		if (dirs.Any(d => d.Name.Equals("GodotSharp", StringComparison.OrdinalIgnoreCase)) ||
		    !dirs.Any(d => d.Name.StartsWith("data_SharpIDE", StringComparison.OrdinalIgnoreCase)))
		{
			Console.WriteLine("Install directory doesn't appear to be a published SharpIDE instance, aborting update!");
			return;
		}

		Console.WriteLine("Removing old version...");

		foreach (var fileSystemInfo in sharpIdeInstallDir.EnumerateFileSystemInfos())
		{
			if (fileSystemInfo is DirectoryInfo directoryInfo) directoryInfo.Delete(true); else fileSystemInfo.Delete();
		}

		Console.WriteLine("Copying new version...");

		if (OperatingSystem.IsLinux())
		{
			await TarFile.ExtractToDirectoryAsync(newSharpIdeReleaseFilePath, sharpIdeInstallDir.FullName, false);
		}
		else if (OperatingSystem.IsWindows())
		{
			using var archive = await ZipFile.OpenReadAsync(newSharpIdeReleaseFilePath);
			await archive.ExtractToDirectoryAsync(sharpIdeInstallDir.FullName);
		}
	}

	Console.WriteLine("Successfully updated, re-launching SharpIDE...");
	await StartProcessFireAndForget(sharpIdeRunningExecutableFilePath, sharpIdeInstallDir.FullName);
}
catch (Exception ex)
{
	Console.WriteLine();
	Console.WriteLine($"Updating SharpIDE Failed: {ex}");
	return;
}

async Task StartProcessFireAndForget(string fileName, string workingDirectory)
{
	ProcessStartInfo processStartInfo = null;
	if (OperatingSystem.IsWindows())
	{
		processStartInfo = new ProcessStartInfo
		{
			FileName = fileName,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false
		};
	}
	else if (OperatingSystem.IsLinux())
	{
		processStartInfo = new ProcessStartInfo
		{
			FileName = "setsid",
			ArgumentList = { fileName },
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = false,
		};
	}
	else if (OperatingSystem.IsMacOS())
	{
		processStartInfo = new ProcessStartInfo
		{
			FileName = "nohup",
			ArgumentList = { fileName },
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
	}
	var process = Process.Start(processStartInfo);
	// For some reason, on Linux, SharpIDE blows up when opening if this process exits too quickly??
	await Task.Delay(1000);
}
