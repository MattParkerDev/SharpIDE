using System.Collections.Concurrent;
using System.Threading.Channels;
using Ardalis.GuardClauses;
using AsyncReadProcess;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using Breakpoint = SharpIDE.Application.Features.Debugging.Breakpoint;

namespace SharpIDE.Application.Features.Run;

public class RunService(ILogger<RunService> logger, RoslynAnalysis roslynAnalysis)
{
	private readonly ConcurrentDictionary<SharpIdeProjectModel, SemaphoreSlim> _projectLocks = [];
	public ConcurrentDictionary<SharpIdeFile, List<Breakpoint>> Breakpoints { get; } = [];
	private Debugger? _debugger; // TODO: Support multiple debuggers for multiple running projects

	private readonly ILogger<RunService> _logger = logger;
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;

	public async Task RunProject(SharpIdeProjectModel project, bool isDebug = false, string? debuggerExecutablePath = null)
	{
		Guard.Against.Null(project, nameof(project));
		Guard.Against.NullOrWhiteSpace(project.FilePath, nameof(project.FilePath), "Project file path cannot be null or empty.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var semaphoreSlim = _projectLocks.GetOrAdd(project, new SemaphoreSlim(1, 1));
		var waitResult = await semaphoreSlim.WaitAsync(0).ConfigureAwait(false);
		if (waitResult is false) throw new InvalidOperationException($"Project {project.Name} is already running.");
		if (project.RunningCancellationTokenSource is not null) throw new InvalidOperationException($"Project {project.Name} is already running with a cancellation token source.");

		project.RunningCancellationTokenSource = new CancellationTokenSource();
		var launchProfiles = await LaunchSettingsParser.GetLaunchSettingsProfiles(project);
		var launchProfile = launchProfiles.FirstOrDefault();
		try
		{
			var processStartInfo = new ProcessStartInfo2
			{
				FileName = "dotnet",
				WorkingDirectory = Path.GetDirectoryName(project.FilePath),
				//Arguments = $"run --project \"{project.FilePath}\" --no-restore",
				Arguments = await GetRunArguments(project),
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				EnvironmentVariables = []
			};
			processStartInfo.EnvironmentVariables["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1";
			// processStartInfo.EnvironmentVariables["TERM"] = "xterm"; // may be necessary on linux/macOS
			if (launchProfile is not null)
			{
				foreach (var envVar in launchProfile.EnvironmentVariables)
				{
					processStartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
				}
				if (launchProfile.ApplicationUrl != null) processStartInfo.EnvironmentVariables["ASPNETCORE_URLS"] = launchProfile.ApplicationUrl;
			}
			if (isDebug)
			{
				processStartInfo.EnvironmentVariables["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
			}

			var process = new Process2
			{
				StartInfo = processStartInfo
			};

			process.Start();

			project.RunningOutputChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = false,
			});
			var logsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_ = Task.Run(async () =>
			{
				await foreach(var log in process.CombinedOutputChannel.Reader.ReadAllAsync().ConfigureAwait(false))
				{
					//var logString = System.Text.Encoding.UTF8.GetString(log, 0, log.Length);
					//Console.Write(logString);
					await project.RunningOutputChannel.Writer.WriteAsync(log).ConfigureAwait(false);
				}
				project.RunningOutputChannel.Writer.Complete();
				logsDrained.TrySetResult();
			});

			if (isDebug)
			{
				// Attach debugger (which internally uses a DiagnosticClient to resume startup)
				var debugger = new Debugger { Project = project, ProcessId = process.ProcessId };
				_debugger = debugger;
				await debugger.Attach(debuggerExecutablePath, Breakpoints.ToDictionary(), project, project.RunningCancellationTokenSource.Token).ConfigureAwait(false);
			}

			project.Running = true;
			project.OpenInRunPanel = true;
			if (isDebug)
			{
				GlobalEvents.Instance.ProjectStartedDebugging.InvokeParallelFireAndForget(project);
			}
			else
			{
				GlobalEvents.Instance.ProjectsRunningChanged.InvokeParallelFireAndForget();
				GlobalEvents.Instance.StartedRunningProject.InvokeParallelFireAndForget();
				GlobalEvents.Instance.ProjectStartedRunning.InvokeParallelFireAndForget(project);
			}
			project.ProjectStartedRunning.InvokeParallelFireAndForget();
			await process.WaitForExitAsync().WaitAsync(project.RunningCancellationTokenSource.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			if (project.RunningCancellationTokenSource.IsCancellationRequested)
			{
				process.End();
				await process.WaitForExitAsync().ConfigureAwait(false);
			}

			await logsDrained.Task.ConfigureAwait(false);
			project.RunningCancellationTokenSource.Dispose();
			project.RunningCancellationTokenSource = null;
			project.Running = false;
			if (isDebug)
			{
				GlobalEvents.Instance.ProjectStoppedDebugging.InvokeParallelFireAndForget(project);
			}
			else
			{
				GlobalEvents.Instance.ProjectsRunningChanged.InvokeParallelFireAndForget();
				GlobalEvents.Instance.ProjectStoppedRunning.InvokeParallelFireAndForget(project);
			}

			project.ProjectStoppedRunning.InvokeParallelFireAndForget();

			_logger.LogInformation("Process for project {ProjectName} has exited", project.Name);
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

		await project.RunningCancellationTokenSource.CancelAsync().ConfigureAwait(false);
	}

	public async Task SendDebuggerStepOver(int threadId) => await _debugger!.StepOver(threadId);
	public async Task SendDebuggerStepInto(int threadId) => await _debugger!.StepInto(threadId);
	public async Task SendDebuggerStepOut(int threadId) => await _debugger!.StepOut(threadId);
	public async Task SendDebuggerContinue(int threadId) => await _debugger!.Continue(threadId);

	public async Task<List<ThreadModel>> GetThreadsAtStopPoint()
	{
		return await _debugger!.GetThreadsAtStopPoint();
	}
	public async Task<List<StackFrameModel>> GetStackFrames(int threadId)
	{
		return await _debugger!.GetStackFramesForThread(threadId);
	}
	public async Task<List<Variable>> GetVariablesForStackFrame(int frameId)
	{
		return await _debugger!.GetVariablesForStackFrame(frameId);
	}

	private async Task<string> GetRunArguments(SharpIdeProjectModel project)
	{
		var dllFullPath = await _roslynAnalysis.GetOutputDllPathForProject(project);
		if (project.IsBlazorProject)
		{
			var blazorDevServerVersion = project.BlazorDevServerVersion;
			// TODO: Naive implementation which doesn't handle a relocated NuGet package cache
			var blazorDevServerDllPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".nuget",
				"packages",
				"microsoft.aspnetcore.components.webassembly.devserver",
				blazorDevServerVersion,
				"tools",
				"blazor-devserver.dll");
			var blazorDevServerFile = new FileInfo(blazorDevServerDllPath);
			if (blazorDevServerFile.Exists is false) throw new FileNotFoundException($"Blazor dev server not found at expected path: {blazorDevServerDllPath}");
			// C:/Users/Matthew/.nuget/packages/microsoft.aspnetcore.components.webassembly.devserver/9.0.7/tools/blazor-devserver.dll --applicationpath C:\Users\Matthew\Documents\Git\BlazorCodeBreaker\artifacts\bin\WebUi\debug\WebUi.dll
			return $" \"{blazorDevServerFile.FullName}\" --applicationpath  \"{dllFullPath}\"";
		}
		return $"\"{dllFullPath}\"";
	}
}
