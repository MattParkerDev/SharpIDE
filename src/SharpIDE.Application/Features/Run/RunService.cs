using System.Diagnostics;
using Ardalis.GuardClauses;
using AsyncReadProcess.Common;
using AsyncReadProcess;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Run;

public class RunService
{
	public HashSet<SharpIdeProjectModel> RunningProjects { get; } = [];
	// TODO: optimise this Copilot junk
	public async Task RunProject(SharpIdeProjectModel project)
	{
		Guard.Against.Null(project, nameof(project));
		Guard.Against.NullOrWhiteSpace(project.FilePath, nameof(project.FilePath), "Project file path cannot be null or empty.");

		var processStartInfo = new ProcessStartInfo2
		{
			FileName = "dotnet",
			Arguments = $"run --project \"{project.FilePath}\" --no-build",
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		await using var process = new AsyncReadProcess.Process2()
		{
			StartInfo = processStartInfo
		};

		process.Start();

		_ = Task.Run(async () =>
		{
			await foreach(var log in process.CombinedOutputChannel.Reader.ReadAllAsync())
			{
				var logString = System.Text.Encoding.UTF8.GetString(log, 0, log.Length);
				Console.Write(logString);
			}
		});


		await process.WaitForExitAsync();

		Console.WriteLine("Project ran successfully.");
	}
}
