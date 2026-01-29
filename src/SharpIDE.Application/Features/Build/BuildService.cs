using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Logging;

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

public class BuildService(ILogger<BuildService> logger)
{
	private readonly ILogger<BuildService> _logger = logger;

	public EventWrapper<BuildStartedFlags, Task> BuildStarted { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<Task> BuildFinished { get; } = new(() => Task.CompletedTask);
	public ChannelTextWriter BuildTextWriter { get; } = new ChannelTextWriter();
	private CancellationTokenSource? _cancellationTokenSource;
	public async Task<SharpIdeBuildResult> MsBuildAsync(string solutionOrProjectFilePath, BuildType buildType = BuildType.Build, BuildStartedFlags buildStartedFlags = BuildStartedFlags.UserFacing, CancellationToken cancellationToken = default)
	{
		if (_cancellationTokenSource is not null) throw new InvalidOperationException("A build is already in progress.");
		_cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(BuildService)}.{nameof(MsBuildAsync)}");

		var terminalLogger = InternalTerminalLoggerFactory.CreateLogger(BuildTextWriter);

		var nodesToBuildWith = GetBuildNodeCount(Environment.ProcessorCount);
		var buildParameters = new BuildParameters
		{
			MaxNodeCount = nodesToBuildWith,
			DisableInProcNode = true,
			Loggers =
			[
				//new BinaryLogger { Parameters = "msbuild.binlog" },
				//new ConsoleLogger(LoggerVerbosity.Minimal) {Parameters = "FORCECONSOLECOLOR"},
				terminalLogger
				//new InMemoryLogger(LoggerVerbosity.Normal)
			],
		};

		var targetsToBuild = TargetsToBuild(buildType);
		var buildRequest = new BuildRequestData(
			projectFullPath : solutionOrProjectFilePath,
			globalProperties: new Dictionary<string, string?>(),
			toolsVersion: null,
			targetsToBuild: targetsToBuild,
			hostServices: null,
			flags: BuildRequestDataFlags.None);

		BuildStarted.InvokeParallelFireAndForget(buildStartedFlags);
		var timer = Stopwatch.StartNew();
		var buildResult = await BuildManager.DefaultBuildManager.BuildAsync(buildParameters, buildRequest, _cancellationTokenSource.Token).ConfigureAwait(false);
		timer.Stop();
		BuildFinished.InvokeParallelFireAndForget();
		_cancellationTokenSource = null;
		_logger.LogInformation(buildResult.Exception, "Build result: {BuildResult} in {ElapsedMilliseconds}ms", buildResult.OverallResult, timer.ElapsedMilliseconds);
		var mappedResult = buildResult.OverallResult switch
		{
			BuildResultCode.Success => SharpIdeBuildResult.Success,
			BuildResultCode.Failure => SharpIdeBuildResult.Failure,
			_ => throw new ArgumentOutOfRangeException()
		};
		return mappedResult;
	}

	public async Task CancelBuildAsync()
	{
		if (_cancellationTokenSource is null) throw new InvalidOperationException("No build is in progress.");
		await _cancellationTokenSource.CancelAsync();
		_cancellationTokenSource = null;
	}

	private static string[] TargetsToBuild(BuildType buildType)
	{
		string[] targetsToBuild = buildType switch
		{
			BuildType.Build => ["Restore", "Build"],
			BuildType.Rebuild => ["Restore", "Rebuild"],
			BuildType.Clean => ["Clean"],
			BuildType.Restore => ["Restore"],
			_ => throw new ArgumentOutOfRangeException(nameof(buildType), buildType, null)
		};
		return targetsToBuild;
	}

	private static int GetBuildNodeCount(int processorCount)
	{
		var nodesToBuildWith = processorCount switch
		{
			1 or 2 => 1,
			3 or 4 => 2,
			>= 5 and <= 10 => processorCount - 2,
			> 10 => processorCount - 4,
			_ => throw new ArgumentOutOfRangeException(nameof(processorCount))
		};
		Guard.Against.NegativeOrZero(nodesToBuildWith, nameof(nodesToBuildWith));
		return nodesToBuildWith;
	}
}
