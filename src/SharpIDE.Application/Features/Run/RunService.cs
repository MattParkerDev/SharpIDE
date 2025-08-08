using System.Collections.Concurrent;
using Ardalis.GuardClauses;
using AsyncReadProcess;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Run;

public class RunService
{
	private readonly ConcurrentDictionary<SharpIdeProjectModel, SemaphoreSlim> _projectLocks = [];
	public async Task RunProject(SharpIdeProjectModel project)
	{
		Guard.Against.Null(project, nameof(project));
		Guard.Against.NullOrWhiteSpace(project.FilePath, nameof(project.FilePath), "Project file path cannot be null or empty.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var semaphoreSlim = _projectLocks.GetOrAdd(project, new SemaphoreSlim(1, 1));
		var waitResult = await semaphoreSlim.WaitAsync(0);
		if (waitResult is false) throw new InvalidOperationException($"Project {project.Name} is already running.");
		if (project.RunningCancellationTokenSource is not null) throw new InvalidOperationException($"Project {project.Name} is already running with a cancellation token source.");

		project.RunningCancellationTokenSource = new CancellationTokenSource();
		try
		{
			var processStartInfo = new ProcessStartInfo2
			{
				FileName = "dotnet",
				Arguments = $"run --project \"{project.FilePath}\" --no-build",
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};

			var process = new AsyncReadProcess.Process2
			{
				StartInfo = processStartInfo
			};

			process.Start();

			var logsDrained = new TaskCompletionSource();
			_ = Task.Run(async () =>
			{
				await foreach(var log in process.CombinedOutputChannel.Reader.ReadAllAsync())
				{
					var logString = System.Text.Encoding.UTF8.GetString(log, 0, log.Length);
					Console.Write(logString);
				}
				logsDrained.TrySetResult();
			});

			project.Running = true;
			project.OpenInRunPanel = true;
			GlobalEvents.InvokeProjectsRunningChanged();
			GlobalEvents.InvokeStartedRunningProject();
			await process.WaitForExitAsync().WaitAsync(project.RunningCancellationTokenSource.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			if (project.RunningCancellationTokenSource.IsCancellationRequested)
			{
				process.End();
				await process.WaitForExitAsync();
			}

			await logsDrained.Task;
			project.Running = false;
			GlobalEvents.InvokeProjectsRunningChanged();

			Console.WriteLine("Project finished running");
		}
		finally
		{
			semaphoreSlim.Release();
		}
	}

	public async Task CancelRunningProject(SharpIdeProjectModel project)
	{
		Guard.Against.Null(project, nameof(project));
		if (project.Running is false) throw new InvalidOperationException($"Project {project.Name} is not running.");
		if (project.RunningCancellationTokenSource is null) throw new InvalidOperationException($"Project {project.Name} does not have a running cancellation token source.");

		await project.RunningCancellationTokenSource.CancelAsync();
		project.RunningCancellationTokenSource.Dispose();
		project.RunningCancellationTokenSource = null;
	}
}
