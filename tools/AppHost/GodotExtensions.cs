using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppHost;

// https://github.com/maddymontaquila/Godot-SnakeCS/blob/maddy/aspire/AppHost/GodotExtensions.cs
public static class GodotExtensions
{
    /// <summary>
    /// Adds a Godot project to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="projectPath">The path to the Godot project file (.godot).</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="args">Optional arguments to pass to the Godot executable.</param>
    /// <returns>A reference to the Godot project resource.</returns>
    public static IResourceBuilder<ExecutableResource> AddGodot(
        this IDistributedApplicationBuilder builder,
        string projectPath,
        string? name = null,
        params string[] args)
    {
        name ??= Path.GetFileNameWithoutExtension(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? ".";
        var godotPath = GetGodotExecutablePath();

        // Build the arguments list
        var arguments = new List<string>
        {
            "--path",
            projectDirectory,
            //"--verbose"
        };

        // Add any custom args
        arguments.AddRange(args);

        // Create a standard ExecutableResource (since custom ones don't seem to work well)
        var godotResource = builder.AddExecutable(
            name: name,
            command: godotPath,
            workingDirectory: projectDirectory,
            args: arguments.ToArray());
            //.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT environment variable is not set"));

        // Add a lifecycle hook to handle building before startup
        builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(sp =>
            new GodotBuildEventSubscriber(
                projectPath,
                projectDirectory,
                sp.GetRequiredService<ILogger<GodotBuildEventSubscriber>>()));

        return godotResource;
    }

    /// <summary>
    /// Gets the Godot executable path based on the current platform.
    /// </summary>
    private static string GetGodotExecutablePath()
    {
        // First check for GODOT environment variable
        var godotPath = Environment.GetEnvironmentVariable("GODOT");
        if (!string.IsNullOrWhiteSpace(godotPath))
        {
            return godotPath;
        }

        // Fallback to platform-specific defaults
        if (OperatingSystem.IsMacOS())
        {
            return "godot";
        }
        else if (OperatingSystem.IsWindows())
        {
            return "godot.exe";
        }
        else if (OperatingSystem.IsLinux())
        {
            return "godot";
        }

        throw new PlatformNotSupportedException("Current platform is not supported for Godot execution.");
    }

    private class GodotBuildEventSubscriber : IDistributedApplicationEventingSubscriber
    {
        private readonly string _projectPath;
        private readonly string _projectDirectory;
        private readonly ILogger<GodotBuildEventSubscriber> _logger;

        public GodotBuildEventSubscriber(string projectPath, string projectDirectory, ILogger<GodotBuildEventSubscriber> logger)
        {
            _projectPath = projectPath;
            _projectDirectory = projectDirectory;
            _logger = logger;
        }
        public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
            {
                _logger.LogInformation("Building Godot project: {ProjectPath}", _projectPath);

                // Execute dotnet build on the project
                var buildProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"build \"{_projectPath}\" --configuration Debug",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = _projectDirectory
                    }
                };

                buildProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogInformation("[Godot Build] {Data}", e.Data);
                    }
                };

                buildProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogError("[Godot Build] {Data}", e.Data);
                    }
                };

                buildProcess.Start();
                buildProcess.BeginOutputReadLine();
                buildProcess.BeginErrorReadLine();
                await buildProcess.WaitForExitAsync(ct);

                if (buildProcess.ExitCode != 0)
                {
                    _logger.LogError("Failed to build Godot project: {ProjectPath}", _projectPath);
                    return;
                }

                _logger.LogInformation("Successfully built Godot project: {ProjectPath}", _projectPath);
            });

            return Task.CompletedTask;
        }
    }
}
