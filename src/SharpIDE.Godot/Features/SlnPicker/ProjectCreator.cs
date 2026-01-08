using System.Diagnostics;
using Godot;
using Directory = System.IO.Directory;
using Exception = System.Exception;
using GD = Godot.GD;
using Path = System.IO.Path;
using System.Text.RegularExpressions; // For parsing SDK list
using System.Collections.Generic; // For lists
using System.Linq; // For sorting and selecting
using File = System.IO.File; // For File.Exists

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class ProjectCreator : Node
{
	// Creates a new project with the specified parameters.
	public void CreateProject(string basePath, string solutionName, string projectName, string template,
		string framework, string language, string sdkVersion, string extraArgs = "")
	{
		solutionName = solutionName ?? "MySolution";
		projectName = projectName ?? "MyProject";
		template = template ?? "console"; //"blazor" for Blazor, "webapi" for API, etc.
		framework = framework ?? "net8.0";
	   language = language ?? "C#";

	   // Normalize and resolve absolute paths for cross-platform reliability
	   basePath = Path.GetFullPath(basePath);
	   string solutionDir = Path.Combine(basePath, solutionName);
	   string projectDir = Path.Combine(solutionDir, projectName);

	   try
	   {
		  string dotnetPath = GetDotnetPath();
		  GD.Print($"Using dotnet CLI path: {dotnetPath}");

		  // Check if dotnet is available at the resolved path
		  if (!IsDotnetAvailable(dotnetPath))
		  {
			 throw new Exception($"dotnet CLI not found at {dotnetPath}. Ensure .NET SDK is installed and update GetDotnetPath() if needed.");
		  }

		  // Require sdkVersion to be specified
		  if (string.IsNullOrEmpty(sdkVersion))
		  {
			  throw new Exception("SDK version must be specified (e.g., '8.0.411').");
		  }

		  // Validate the specified SDK is installed using the system CLI
		  string installedSdks = GetInstalledSdks(dotnetPath);
		  if (!Regex.IsMatch(installedSdks, $@"\b{Regex.Escape(sdkVersion)}\b"))
		  {
			  OS.Alert($"Specified SDK {sdkVersion} not installed. Available: {installedSdks}. Install from https://dotnet.microsoft.com.","Error");
		  }

		  Directory.CreateDirectory(solutionDir);
		  PinGlobalConfig(solutionDir, sdkVersion);

		  // Create solution file
		  ExecuteDotnetCommand(dotnetPath, solutionDir, $"new sln -n {solutionName}");

		  // Create project subdirectory
		  Directory.CreateDirectory(projectDir);

		  // Create project with generic template
		  string projectArgs = $"new {template} -n {projectName} --framework {framework} --language {language} {extraArgs}";
		  ExecuteDotnetCommand(dotnetPath, solutionDir, projectArgs);

		  // Add project to solution
		  var solutionArgs = $"sln add {projectName}/{projectName}.csproj";
		  ExecuteDotnetCommand(dotnetPath, solutionDir, solutionArgs);

		  GD.Print($"Project ({template}) created successfully at {solutionDir}!");
		  PinGlobalConfig(solutionDir, sdkVersion,true);
	   }
	   catch (Exception ex)
	   {
		   OS.Alert($"Error creating project: {ex.Message}. Folder may be partially created at {solutionDir}. If access denied to template cache, try running 'sudo chown -R $(whoami) ~/.templateengine' or 'rm -rf ~/.templateengine/dotnetcli/<version>' in terminal.","Error");
	   }
	}

	// Get platform-specific path to system dotnet CLI
	private string GetDotnetPath()
	{
		string osName = OS.GetName();
		string dotnetPath = "dotnet"; // Fallback

		if (osName == "macOS")
		{
			dotnetPath = "/usr/local/share/dotnet/dotnet";
		}
		else if (osName == "Linux")
		{
			dotnetPath = "/usr/share/dotnet/dotnet";
		}
		else if (osName == "Windows")
		{
			dotnetPath = @"C:\Program Files\dotnet\dotnet.exe";
		}

		if (File.Exists(dotnetPath))
		{
			return dotnetPath;
		}
		else
		{
			OS.Alert($"System dotnet not found at {dotnetPath}; falling back to 'dotnet'. Check your install.","Warning");
			return "dotnet";
		}
	}

	// Get list of installed SDKs using specified dotnetPath
	private string GetInstalledSdks(string dotnetPath)
	{
		var processInfo = new ProcessStartInfo
		{
			FileName = dotnetPath,
			Arguments = "--list-sdks",
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using (var process = Process.Start(processInfo))
		{
			process?.WaitForExit();
			string output = process?.StandardOutput.ReadToEnd() ?? "";
			// Parse versions (e.g., lines like "8.0.411 [/path]") with Multiline option
			var versions = Regex.Matches(output, @"^(\d+\.\d+\.\d+)", RegexOptions.Multiline);
			return string.Join(", ", versions.Select(m => m.Groups[1].Value));
		}
	}

	// Check if dotnet is available using specified dotnetPath
	private bool IsDotnetAvailable(string dotnetPath)
	{
	   try
	   {
		  var processInfo = new ProcessStartInfo
		  {
			 FileName = dotnetPath,
			 Arguments = "--version",
			 RedirectStandardOutput = true,
			 UseShellExecute = false,
			 CreateNoWindow = true
		  };
		  using (var process = Process.Start(processInfo))
		  {
			 process?.WaitForExit();
			 return process?.ExitCode == 0;
		  }
	   }
	   catch
	   {
		  return false;
	   }
	}

	// Execute dotnet commands using specified dotnetPath
	private void ExecuteDotnetCommand(string dotnetPath, string workingDir, string arguments)
	{
	   workingDir = Path.GetFullPath(workingDir); // Ensure absolute for OS consistency
	   var processInfo = new ProcessStartInfo
	   {
		  FileName = dotnetPath,
		  Arguments = arguments,
		  WorkingDirectory = workingDir,
		  RedirectStandardOutput = true,
		  RedirectStandardError = true,
		  UseShellExecute = false,
		  CreateNoWindow = true
	   };

	   processInfo.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";
	   
	   using (var process = new Process { StartInfo = processInfo })
	   {
		  process.Start();
		  string output = process.StandardOutput.ReadToEnd();
		  string error = process.StandardError.ReadToEnd();
		  process.WaitForExit();
		  
		  if (process.ExitCode != 0) OS.Alert($"dotnet command failed in {workingDir}: {error}", "Error");
		  // if (!string.IsNullOrEmpty(error)) OS.Alert($"Command Error: {error}", "Error");;

		  GD.Print($"dotnet output in {workingDir}: {output}");
	   }
	}

	private void PinGlobalConfig(string solutionDir, string sdkVersion, bool deleteExistingGlobalJson = false)
	{
		if (!deleteExistingGlobalJson)
		{
			// Create global.json to pin the specified SDK
			string globalJsonPath = Path.Combine(solutionDir, "global.json");
			string globalJsonContent = $@"{{
""sdk"": {{
	""version"": ""{sdkVersion}""
	}}
}}";
			File.WriteAllText(globalJsonPath, globalJsonContent);
			GD.Print($"Pinned SDK to {sdkVersion} via global.json in {solutionDir}");
		}
		else
		{
			string globalJsonPath = Path.Combine(solutionDir, "global.json");
			if (File.Exists(globalJsonPath))
			{
				File.Delete(globalJsonPath);
				GD.Print($"Deleted global.json from {solutionDir}");
			}
		}
		
	}
}
