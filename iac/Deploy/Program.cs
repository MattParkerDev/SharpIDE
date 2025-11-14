using Deploy.Steps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ParallelPipelines.Host;
using ParallelPipelines.Host.Helpers;

var builder = Host.CreateApplicationBuilder(args);

builder
	.Configuration.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", false)
	.AddUserSecrets<Program>()
	.AddEnvironmentVariables();

builder.Services.AddParallelPipelines(
	builder.Configuration,
	config =>
	{
		config.Local.OutputSummaryToFile = true;
		config.Cicd.OutputSummaryToGithubStepSummary = true;
		config.Cicd.WriteCliCommandOutputsToSummary = true;
		config.AllowedEnvironmentNames = ["prod"];
	}
);
builder.Services
	.AddStep<RestoreAndBuildStep>()
	.AddStep<CreateWindowsRelease>()
	;

// Unhandled exception. System.InvalidOperationException: fatal: detected dubious ownership in repository at '/__w/SharpIDE/SharpIDE'
// To add an exception for this directory, call:
// git config --global --add safe.directory /__w/SharpIDE/SharpIDE
await PipelineCliHelper.RunCliCommandAsync("git", "config --global --add safe.directory /__w/SharpIDE/SharpIDE", CancellationToken.None);

using var host = builder.Build();

await host.RunAsync();
