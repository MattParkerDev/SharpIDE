using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Events;
using SharpIDE.MsBuildHost.Contracts;

// ReSharper disable InconsistentlySynchronizedField

namespace SharpIDE.Application.Features.Build;

public enum BuildType
{
	Build,
	Rebuild,
	Clean,
	Restore
}
public enum BuildStartedFlags { UserFacing = 0, Internal }
public enum SharpIdeBuildResult { Success = 0, Failure }

public partial class BuildService(ILogger<BuildService> logger) : IDisposable
{
	private readonly ILogger<BuildService> _logger = logger;

	public EventWrapper<BuildStartedFlags, Task> BuildStarted { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<Task> BuildFinished { get; } = new(() => Task.CompletedTask);
	public PipeReader? BuildLogPipeReader { get; private set; }

	private CancellationTokenSource? _cancellationTokenSource;
	private IRpcBuildService? _rpcBuildService;

	public async Task<SharpIdeBuildResult> MsBuildAsync(string solutionOrProjectFilePath, BuildType buildType = BuildType.Build, BuildStartedFlags buildStartedFlags = BuildStartedFlags.UserFacing, CancellationToken cancellationToken = default)
	{
		var buildTypeDto = buildType switch
		{
			BuildType.Build => BuildTypeDto.Build,
			BuildType.Rebuild => BuildTypeDto.Rebuild,
			BuildType.Clean => BuildTypeDto.Clean,
			BuildType.Restore => BuildTypeDto.Restore,
			_ => throw new ArgumentOutOfRangeException(nameof(buildType), buildType, null)
		};
		var (buildResult, exception) = await RunBuildAsync((rpc, ct) => rpc.MsBuildAsync(solutionOrProjectFilePath, buildTypeDto, ct), buildStartedFlags, cancellationToken).ConfigureAwait(false);
		_logger.LogInformation(exception, "Build result: {BuildResult}", buildResult);
		return MapResult(buildResult);
	}

	/// Restores and builds the project (not the solution) with Build;ComputeRunArguments, returning the properties needed to launch it
	public async Task<(SharpIdeBuildResult BuildResult, IReadOnlyDictionary<string, string> RunProperties)> BuildProjectForRunAsync(string projectFilePath, string? targetFramework, BuildStartedFlags buildStartedFlags = BuildStartedFlags.UserFacing, CancellationToken cancellationToken = default)
	{
		var result = await RunBuildAsync((rpc, ct) => rpc.BuildProjectForRunAsync(projectFilePath, targetFramework, ct), buildStartedFlags, cancellationToken).ConfigureAwait(false);
		if (result.ErrorMessage is not null) _logger.LogWarning("Build for run failed: {ErrorMessage}", result.ErrorMessage);
		_logger.LogInformation("Build for run result: {BuildResult}", result.BuildResult);
		return (MapResult(result.BuildResult), result.Properties);
	}

	private async Task<T> RunBuildAsync<T>(Func<IRpcBuildService, CancellationToken, Task<T>> build, BuildStartedFlags buildStartedFlags, CancellationToken cancellationToken)
	{
		var rpcBuildService = await GetRpcAsync(cancellationToken);
		if (_cancellationTokenSource is not null) throw new InvalidOperationException("A build is already in progress.");
		_cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(BuildService)}.{nameof(RunBuildAsync)}");

		BuildStarted.InvokeParallelFireAndForget(buildStartedFlags);
		var timer = Stopwatch.StartNew();
		try
		{
			return await build(rpcBuildService, _cancellationTokenSource.Token).ConfigureAwait(false);
		}
		finally
		{
			timer.Stop();
			_cancellationTokenSource = null;
			BuildFinished.InvokeParallelFireAndForget();
			_logger.LogInformation("Build finished in {ElapsedMilliseconds}ms", timer.ElapsedMilliseconds);
		}
	}

	private static SharpIdeBuildResult MapResult(BuildResultDto buildResult) => buildResult switch
	{
		BuildResultDto.Success => SharpIdeBuildResult.Success,
		BuildResultDto.Failure => SharpIdeBuildResult.Failure,
		_ => throw new ArgumentOutOfRangeException(nameof(buildResult), buildResult, null)
	};

	public async Task CancelBuildAsync()
	{
		if (_cancellationTokenSource is null) throw new InvalidOperationException("No build is in progress.");
		await _cancellationTokenSource.CancelAsync();
		_cancellationTokenSource = null;
	}
}
