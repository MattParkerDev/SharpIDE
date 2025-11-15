using Deploy.Steps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ParallelPipelines.Host;

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

using var host = builder.Build();

await host.RunAsync();
