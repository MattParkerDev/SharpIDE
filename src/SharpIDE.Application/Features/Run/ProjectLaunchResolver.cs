using System.Text;

namespace SharpIDE.Application.Features.Run;

public sealed record ProjectLaunch(string FileName, List<string> Arguments, string WorkingDirectory);

/// Merges the run properties from a Build;ComputeRunArguments build with the launch profile
public static class ProjectLaunchResolver
{
	public static ProjectLaunch Resolve(string projectDirectory, string? outputType, IReadOnlyDictionary<string, string> runProperties, ProjectLaunchSettingsModel? launchProfile)
	{
		var profileArguments = NormaliseProfileArguments(launchProfile?.CommandLineArgs);

		if (launchProfile?.CommandName is "Executable")
		{
			if (string.IsNullOrWhiteSpace(launchProfile.ExecutablePath)) throw new InvalidOperationException($"Launch profile '{launchProfile.LaunchProfileName}' has commandName Executable, but no executablePath");
			var executableWorkingDirectory = ResolveRelativeTo(projectDirectory, ExpandProjectDir(launchProfile.WorkingDirectory, projectDirectory)) ?? projectDirectory;
			return new ProjectLaunch(ExpandProjectDir(launchProfile.ExecutablePath, projectDirectory)!, SplitCommandLine(profileArguments), executableWorkingDirectory);
		}

		var runCommand = runProperties.GetValueOrDefault("RunCommand");
		var targetPath = runProperties.GetValueOrDefault("TargetPath");
		string executable;
		if (string.IsNullOrWhiteSpace(runCommand) is false)
		{
			executable = runCommand;
		}
		else if (outputType is "Library" || string.IsNullOrWhiteSpace(targetPath))
		{
			throw new InvalidOperationException("The project cannot be run - it doesn't set RunCommand, and is a library");
		}
		else
		{
			executable = targetPath;
		}
		executable = ResolveExecutable(executable);

		var arguments = runProperties.GetValueOrDefault("RunArguments") ?? string.Empty;
		if (string.IsNullOrWhiteSpace(profileArguments) is false)
		{
			arguments = string.IsNullOrWhiteSpace(arguments) ? profileArguments : $"{arguments} {profileArguments}";
		}

		// The profile wins, then RunWorkingDirectory, then OutDir - each relative to the project folder
		var workingDirectory =
			ResolveRelativeTo(projectDirectory, ExpandProjectDir(launchProfile?.WorkingDirectory, projectDirectory))
			?? ResolveRelativeTo(projectDirectory, runProperties.GetValueOrDefault("RunWorkingDirectory"))
			?? ResolveRelativeTo(projectDirectory, runProperties.GetValueOrDefault("OutDir"))
			?? projectDirectory;

		return new ProjectLaunch(executable, SplitCommandLine(arguments), workingDirectory);
	}

	private static string? NormaliseProfileArguments(string? commandLineArgs) =>
		commandLineArgs?.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

	private static string? ExpandProjectDir(string? value, string projectDirectory) =>
		value?.Replace("$(ProjectDir)", Path.TrimEndingDirectorySeparator(projectDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	private static string? ResolveRelativeTo(string projectDirectory, string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;
		return Path.GetFullPath(path, projectDirectory);
	}

	/// On Windows, .exe is appended if there is no extension (e.g. dotnet). A bare name (no directory separator) is looked up on PATH.
	internal static string ResolveExecutable(string executable)
	{
		if (OperatingSystem.IsWindows() && Path.HasExtension(executable) is false) executable += ".exe";
		if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar)) return executable;

		var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (var directory in pathDirectories)
		{
			var candidate = Path.Combine(directory.Trim('"'), executable);
			if (File.Exists(candidate)) return candidate;
		}
		return executable;
	}

	/// Splits a command line string into arguments, with the same rules as ProcessStartInfo.Arguments (the Windows CommandLineToArgvW rules, on every OS) -
	/// RunArguments is written by the SDK to be passed that way
	internal static List<string> SplitCommandLine(string? commandLine)
	{
		var results = new List<string>();
		if (string.IsNullOrWhiteSpace(commandLine)) return results;

		var currentArgument = new StringBuilder();
		var inQuotes = false;
		var hasArgument = false;
		var i = 0;
		while (i < commandLine.Length)
		{
			var c = commandLine[i];
			if (c is '\\')
			{
				var backslashCount = 0;
				while (i < commandLine.Length && commandLine[i] is '\\')
				{
					backslashCount++;
					i++;
				}
				if (i < commandLine.Length && commandLine[i] is '"')
				{
					// 2n backslashes + quote -> n backslashes, and the quote toggles. 2n+1 -> n backslashes and a literal quote.
					currentArgument.Append('\\', backslashCount / 2);
					if (backslashCount % 2 is 1)
					{
						currentArgument.Append('"');
						i++;
					}
				}
				else
				{
					currentArgument.Append('\\', backslashCount);
				}
				hasArgument = true;
				continue;
			}
			if (c is '"')
			{
				// "" inside quotes is a literal quote
				if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] is '"')
				{
					currentArgument.Append('"');
					i += 2;
					continue;
				}
				inQuotes = !inQuotes;
				hasArgument = true;
				i++;
				continue;
			}
			if (char.IsWhiteSpace(c) && inQuotes is false)
			{
				if (hasArgument)
				{
					results.Add(currentArgument.ToString());
					currentArgument.Clear();
					hasArgument = false;
				}
				i++;
				continue;
			}
			currentArgument.Append(c);
			hasArgument = true;
			i++;
		}
		if (hasArgument) results.Add(currentArgument.ToString());
		return results;
	}
}
