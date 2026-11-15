using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading.Channels;
using Ardalis.GuardClauses;
using AsyncReadProcess;
using Microsoft.Extensions.Logging;
using SharpDbg.DapProtocol;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Run;

public partial class RunService(ILogger<RunService> logger, RoslynAnalysis roslynAnalysis, BuildService buildService, DebuggingService debuggingService)
{
	private const int DefaultTerminalCols = 120;
	private const int DefaultTerminalRows = 30;
	private readonly ConcurrentDictionary<SharpIdeProjectModel, SemaphoreSlim> _projectLocks = [];
	private readonly ConcurrentDictionary<SharpIdeProjectModel, DebuggerSessionId> _projectDebuggerSessionIds = [];

	private readonly ILogger<RunService> _logger = logger;
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly BuildService _buildService = buildService;
	private readonly DebuggingService _debuggingService = debuggingService;

	/// <param name="usePty">Attach the process to a pseudo terminal, enabling input, Ctrl+C, terminal resizing etc. When false, output is redirected via pipes, and input is not supported</param>
	public async Task RunProject(SharpIdeProjectModel project, bool isDebug = false, DebuggerExecutableInfo? debuggerExecutableInfo = null, bool usePty = true)
	{
		Guard.Against.Null(project);
		Guard.Against.NullOrWhiteSpace(project.FilePath, nameof(project.FilePath), "Project file path cannot be null or empty.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var semaphoreSlim = _projectLocks.GetOrAdd(project, new SemaphoreSlim(1, 1));
		var waitResult = await semaphoreSlim.WaitAsync(0).ConfigureAwait(false);
		if (waitResult is false) throw new InvalidOperationException($"Project {project.Name.Value} is already running.");

		try
		{
			if (project.RunningCancellationTokenSource is not null) throw new InvalidOperationException($"Project {project.Name.Value} is already running with a cancellation token source.");

			// Multi-TFM projects are built (and run) for the TFM active in the editor
			var activeSnapshot = project.ActiveMsBuildProjectLoadResult.Value.Snapshot;
			var targetFramework = project.TfmSpecificLoadResults.Value.Count > 0 ? activeSnapshot?.GetPropertyValue("TargetFramework") : null;
			var (buildResult, runProperties) = await _buildService.BuildProjectForRunAsync(project.FilePath, targetFramework);
			if (buildResult is not SharpIdeBuildResult.Success)
			{
				_logger.LogInformation("Build failed for project {ProjectName}. Aborting run/debug.", project.Name.Value);
				project.ProjectRunFailed.InvokeParallelFireAndForget();
				return;
			}

			project.RunningCancellationTokenSource = new CancellationTokenSource();

			var launchProfiles = await LaunchSettingsParser.GetLaunchSettingsProfiles(project);
			var launchProfile = launchProfiles.FirstOrDefault();
			var outputType = activeSnapshot?.GetPropertyValue("OutputType");
			var launch = ProjectLaunchResolver.Resolve(project.DirectoryPath, outputType, runProperties, launchProfile);

			var processStartInfo = new ProcessStartInfo2
			{
				FileName = launch.FileName,
				WorkingDirectory = launch.WorkingDirectory,
				Arguments = launch.Arguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = usePty, // required when UsePty is true
				UsePty = usePty,
				TerminalSize = usePty ? (DefaultTerminalCols, DefaultTerminalRows) : null, // corrected to the actual terminal control size once the run/debug tab attaches
				EnvironmentVariables = []
			};
			processStartInfo.EnvironmentVariables["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1";
			if (usePty && OperatingSystem.IsWindows() is false) processStartInfo.EnvironmentVariables["TERM"] = "xterm-256color";
			if (launchProfile is not null)
			{
				if (launchProfile.ApplicationUrl != null) processStartInfo.EnvironmentVariables["ASPNETCORE_URLS"] = launchProfile.ApplicationUrl;
				// Applied last, so the profile wins
				foreach (var envVar in launchProfile.EnvironmentVariables)
				{
					processStartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
				}
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

			project.ProcessStandardIo = new StandardIo(process.CombinedOutputPipeReader, process.StandardInputPipeWriter, isPty: usePty, resizeTerminal: usePty ? process.ResizeTerminal : null);

			if (isDebug)
			{
				// Attach debugger (which internally uses a DiagnosticClient to resume startup)
				var debuggerSessionId = await _debuggingService.Attach(process.ProcessId, debuggerExecutableInfo, Breakpoints.ToDictionary(), project, project.RunningCancellationTokenSource.Token);
				if (_projectDebuggerSessionIds.TryAdd(project, debuggerSessionId) is false)
				{
					await _debuggingService.CloseDebuggerSession(debuggerSessionId);
					throw new InvalidOperationException($"Project {project.Name.Value} already has a debugger session.");
				}
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
			await ((Task)process.WaitForExitAsync().WaitAsync(project.RunningCancellationTokenSource.Token)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			if (project.RunningCancellationTokenSource.IsCancellationRequested)
			{
				process.End();
				await process.WaitForExitAsync().ConfigureAwait(false);
			}

			await project.ProcessStandardIo.OutputReadComplete.Task.ConfigureAwait(false);
			await project.ProcessStandardIo.StdinWriteComplete.Task.ConfigureAwait(false);
			project.RunningCancellationTokenSource.Dispose();
			project.RunningCancellationTokenSource = null;
			project.Running = false;
			if (isDebug)
			{
				if (_projectDebuggerSessionIds.TryRemove(project, out var debuggerSessionId) is false) throw new InvalidOperationException($"Project {project.Name.Value} does not have a debugger session.");
				await _debuggingService.CloseDebuggerSession(debuggerSessionId);
				GlobalEvents.Instance.ProjectStoppedDebugging.InvokeParallelFireAndForget(project);
			}
			else
			{
				GlobalEvents.Instance.ProjectsRunningChanged.InvokeParallelFireAndForget();
				GlobalEvents.Instance.ProjectStoppedRunning.InvokeParallelFireAndForget(project);
			}

			project.ProjectStoppedRunning.InvokeParallelFireAndForget();

			_logger.LogInformation("Process for project {ProjectName} has exited", project.Name.Value);
		}
		catch
		{
			if (_projectDebuggerSessionIds.TryRemove(project, out var debuggerSessionId))
			{
				try
				{
					await _debuggingService.CloseDebuggerSession(debuggerSessionId);
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, "Failed to close debugger session for project {ProjectName}", project.Name.Value);
				}
				GlobalEvents.Instance.ProjectStoppedDebugging.InvokeParallelFireAndForget(project);
			}
			project.RunningCancellationTokenSource?.Dispose();
			project.RunningCancellationTokenSource = null;
			project.ProjectRunFailed.InvokeParallelFireAndForget();
			throw;
		}
		finally
		{
			semaphoreSlim.Release();
		}
	}

	public async Task CancelRunningProject(SharpIdeProjectModel project)
	{
		Guard.Against.Null(project);
		if (project.Running is false) throw new InvalidOperationException($"Project {project.Name.Value} is not running.");
		if (project.RunningCancellationTokenSource is null) throw new InvalidOperationException($"Project {project.Name.Value} does not have a running cancellation token source.");

		await project.RunningCancellationTokenSource.CancelAsync().ConfigureAwait(false);
	}

	public async Task SendDebuggerStepOver(SharpIdeProjectModel project, int threadId, CancellationToken cancellationToken = default)
	{
		await _debuggingService.StepOver(GetDebuggerSessionId(project), threadId, cancellationToken);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget(project);
	}
	public async Task SendDebuggerStepInto(SharpIdeProjectModel project, int threadId, CancellationToken cancellationToken = default)
	{
		await _debuggingService.StepInto(GetDebuggerSessionId(project), threadId, cancellationToken);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget(project);
	}
	public async Task SendDebuggerStepOut(SharpIdeProjectModel project, int threadId, CancellationToken cancellationToken = default)
	{
		await _debuggingService.StepOut(GetDebuggerSessionId(project), threadId, cancellationToken);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget(project);
	}
	public async Task SendDebuggerContinue(SharpIdeProjectModel project, int threadId, CancellationToken cancellationToken = default)
	{
		await _debuggingService.Continue(GetDebuggerSessionId(project), threadId, cancellationToken);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget(project);
	}

	public async Task<List<ThreadModel>> GetThreadsAtStopPoint(SharpIdeProjectModel project)
	{
		return await _debuggingService.GetThreadsAtStopPoint(GetDebuggerSessionId(project));
	}
	public async Task<List<StackFrameModel>> GetStackFrames(SharpIdeProjectModel project, int threadId)
	{
		return await _debuggingService.GetStackFramesForThread(GetDebuggerSessionId(project), threadId);
	}
	public async Task<StackFrameModel> ResolveStackFrame(SharpIdeProjectModel project, StackFrameModel stackFrame)
	{
		return await _debuggingService.ResolveStackFrame(GetDebuggerSessionId(project), stackFrame);
	}
	public async Task<List<Variable>> GetVariablesForStackFrame(SharpIdeProjectModel project, int frameId)
	{
		return await _debuggingService.GetVariablesForStackFrame(GetDebuggerSessionId(project), frameId);
	}
	public async Task<List<Variable>> GetVariablesForVariablesReference(SharpIdeProjectModel project, int variablesReferenceId)
	{
		return await _debuggingService.GetVariablesForVariablesReference(GetDebuggerSessionId(project), variablesReferenceId);
	}
	public async Task<EvaluateResponse> EvaluateExpression(SharpIdeProjectModel project, int frameId, string expression)
	{
		return await _debuggingService.EvaluateExpression(GetDebuggerSessionId(project), frameId, expression);
	}

	private DebuggerSessionId GetDebuggerSessionId(SharpIdeProjectModel project)
	{
		Guard.Against.Null(project);
		return _projectDebuggerSessionIds.TryGetValue(project, out var debuggerSessionId)
			? debuggerSessionId
			: throw new InvalidOperationException($"Project {project.Name.Value} does not have a debugger session.");
	}
}
