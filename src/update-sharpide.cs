#!/usr/bin/env dotnet

using System.Diagnostics;
using System.IO.Compression;

try
{
	var sharpIdeInstallPath = args.ElementAtOrDefault(0);
	var sharpIdeRunningExecutableFilePath = args.ElementAtOrDefault(1);
	var newSharpIdeReleaseFilePath = args.ElementAtOrDefault(2);
	var runningIdeInstancePidString = args.ElementAtOrDefault(3);
	if (string.IsNullOrWhiteSpace(runningIdeInstancePidString)) throw new InvalidOperationException("SharpIDE PID was not provided");
	var runningIdeInstancePid = int.Parse(runningIdeInstancePidString);
	var sharpIdeInstallDir = new DirectoryInfo(sharpIdeInstallPath!);
	if (sharpIdeInstallDir.Exists is false) throw new DirectoryNotFoundException($"SharpIDE install directory not found: '{sharpIdeInstallPath}'");
	if (File.Exists(sharpIdeRunningExecutableFilePath) is false) throw new FileNotFoundException("Running SharpIDE executable file not found", sharpIdeRunningExecutableFilePath);
	if (File.Exists(newSharpIdeReleaseFilePath) is false) throw new FileNotFoundException("New SharpIDE release file not found", newSharpIdeReleaseFilePath);

	Console.WriteLine($"Update will be installed at: {sharpIdeInstallPath}");
	Console.WriteLine($"New Release: {newSharpIdeReleaseFilePath}");

	try
	{
		// wait until the runningIdeInstancePid process ends
		var runningIdeProcess = Process.GetProcessById(runningIdeInstancePid);
		if (runningIdeProcess is null) throw new InvalidOperationException("Process not found");
		Console.WriteLine($"Waiting for SharpIDE process (PID: {runningIdeInstancePid}) to exit...");
		await runningIdeProcess.WaitForExitAsync();
	}
	catch (ArgumentException ex)
	{
		// The process already exited
	}

	Console.WriteLine($"SharpIDE process exited, proceeding with update...");

	var dirs = sharpIdeInstallDir.EnumerateDirectories().ToList();

	if (dirs.Any(d => d.Name.Equals("GodotSharp", StringComparison.OrdinalIgnoreCase)) ||
	    !dirs.Any(d => d.Name.StartsWith("data_SharpIDE", StringComparison.OrdinalIgnoreCase)))
	{
		Console.WriteLine("Install directory doesn't appear to be a published SharpIDE instance, aborting update!");
		Console.WriteLine("Press any key to exit...");
		Console.ReadKey();
		return;
	}

	Console.WriteLine("Removing old version...");

	foreach (var fileSystemInfo in sharpIdeInstallDir.EnumerateFileSystemInfos())
	{
        if (fileSystemInfo is DirectoryInfo directoryInfo) directoryInfo.Delete(true); else fileSystemInfo.Delete();
	}

	Console.WriteLine("Copying new version...");

	var archive = await ZipFile.OpenReadAsync(newSharpIdeReleaseFilePath);
	archive.ExtractToDirectory(sharpIdeInstallDir.FullName);

	Console.WriteLine("Successfully updated, re-launching SharpIDE...");
	Process.Start(new ProcessStartInfo()
	{
		FileName = sharpIdeRunningExecutableFilePath,
		WorkingDirectory = sharpIdeInstallDir.FullName,
		UseShellExecute = true
	});
}
catch (Exception ex)
{
	Console.WriteLine();
	Console.WriteLine($"Updating SharpIDE Failed: {ex}");
	Console.WriteLine("Press any key to exit...");
	Console.ReadKey();
	return;
}
