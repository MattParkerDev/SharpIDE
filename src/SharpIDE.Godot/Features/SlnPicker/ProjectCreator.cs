using System.Diagnostics;
using Godot;
using Directory = System.IO.Directory;
using Exception = System.Exception;
using GD = Godot.GD;
using Path = System.IO.Path;

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class ProjectCreator : Node
{
	public void CreateProject(string basePath, string solutionName, string projectName, string template, string framework, string language, string extraArgs = "")
	{
		// Defaults matching screenshot; override via UI
		solutionName = solutionName ?? "MySolution";
		projectName = projectName ?? "MyProject";
		template = template ?? "console"; // e.g., "blazor" for Blazor, "webapi" for API, etc.
		framework = framework ?? "net8.0";
		language = language ?? "C#";

		// Normalize and resolve absolute paths for cross-platform reliability
		basePath = Path.GetFullPath(basePath);
		string solutionDir = Path.Combine(basePath, solutionName);
		string projectDir = Path.Combine(solutionDir, projectName);

		try
		{
			// Check if dotnet is available (cross-platform validation)
			if (!IsDotnetAvailable())
			{
				throw new Exception("dotnet CLI not found. Ensure .NET SDK is installed and in PATH.");
			}

			// Step 1: Create solution directory
			Directory.CreateDirectory(solutionDir);

			// Step 2: Create solution file
			ExecuteDotnetCommand(solutionDir, $"new sln -n {solutionName}");

			// Step 3: Create project subdirectory
			Directory.CreateDirectory(projectDir);

			// Step 4: Create project with generic template
			string projectArgs = $"new {template} -n {projectName} --framework {framework} --language {language} {extraArgs}";
			ExecuteDotnetCommand(projectDir, projectArgs);

			// Step 5: Add project to solution
			ExecuteDotnetCommand(solutionDir, $"sln add {projectName}/{projectName}.csproj");

			GD.Print($"Project ({template}) created successfully at {solutionDir}!");
			// In SharpIDE: Refresh solution explorer, open the project, or trigger build.
		}
		catch (Exception ex)
		{
			GD.PrintErr($"Error creating project: {ex.Message}");
			// In SharpIDE: Show error dialog or log to output panel.
		}
	}

	// Helper to check if dotnet is available
	private bool IsDotnetAvailable()
	{
		try
		{
			var processInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
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

	// Helper for executing dotnet commands
	private void ExecuteDotnetCommand(string workingDir, string arguments)
	{
		workingDir = Path.GetFullPath(workingDir); // Ensure absolute for OS consistency
		var processInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = arguments,
			WorkingDirectory = workingDir,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using (var process = new Process { StartInfo = processInfo })
		{
			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();

			if (process.ExitCode != 0)
			{
				throw new Exception($"dotnet command failed in {workingDir}: {error}");
			}

			GD.Print($"dotnet output in {workingDir}: {output}"); // Log for SharpIDE's console
		}
	}

	// Example: Override _Ready for testing (remove in production)
	public override void _Ready()
	{
		// Test console app
		CreateProject("/path/to/projects", "ConsoleApp1", "ConsoleApp1", "console", "net8.0", "C#");

		// Test Blazor (future expansion): Uncomment to try
		// CreateProject("/path/to/projects", "BlazorApp1", "BlazorApp1", "blazor", "net8.0", "C#", "--no-https");
	}
}
