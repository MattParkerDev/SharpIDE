using Godot;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO; // For Path.GetFullPath

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class GitCommandLine : Node
{
    /// <summary>
    /// Initializes a Git repository in the specified base path by running 'git init'.
    /// Runs asynchronously to avoid blocking the Godot main thread.
    /// </summary>
    /// <param name="basePath">The directory path where the repo should be created.</param>
    /// <returns>A task that completes when the command finishes, with success indicator.</returns>
    public async Task<bool> InitializeGitRepoAsync(string basePath)
    {
        // Normalize and resolve absolute path for cross-platform reliability
        basePath = Path.GetFullPath(basePath);
        // First, check if Git is installed
        bool gitInstalled = await CheckGitInstalledAsync();
        if (!gitInstalled)
        {
            GD.Print("Git is not installed or not found in PATH. Cannot initialize repo.");
            return false;
        }

        // Run 'git init' asynchronously
        (string output, string error, int exitCode) = await RunGitCommandAsync("init", basePath);

        if (exitCode == 0)
        {
            GD.Print($"Git repo initialized successfully in {basePath}. Output: {output}");
            return true;
        }
        else
        {
            GD.Print($"Failed to initialize Git repo. Error: {error}");
            return false;
        }
    }

    /// <summary>
    /// Checks if Git is installed by running 'git --version'.
    /// </summary>
    private async Task<bool> CheckGitInstalledAsync()
    {
        (string output, string error, int exitCode) = await RunGitCommandAsync("--version", Directory.GetCurrentDirectory());
        return exitCode == 0 && !string.IsNullOrEmpty(output);
    }

    /// <summary>
    /// Helper to run any Git command asynchronously in a specified working directory.
    /// Captures output and errors.
    /// </summary>
    private async Task<(string Output, string Error, int ExitCode)> RunGitCommandAsync(string arguments, string workingDirectory)
    {
        return await Task.Run(() =>
        {
            var process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false; // Required for redirection
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true; // Hide console window

            try
            {
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (output, error, process.ExitCode);
            }
            catch (Exception ex)
            {
                GD.Print($"Exception running Git command: {ex.Message}");
                return (string.Empty, ex.Message, -1);
            }
        });
    }
}