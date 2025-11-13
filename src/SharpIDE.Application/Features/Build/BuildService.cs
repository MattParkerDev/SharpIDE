using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Logging;

namespace SharpIDE.Application.Features.Build;

public enum BuildType
{
	Build,
	Rebuild,
	Clean,
	Restore
}
public class BuildService(ILogger<BuildService> logger)
{
	private readonly ILogger<BuildService> _logger = logger;

	public event Func<Task> BuildStarted = () => Task.CompletedTask;
	public ChannelTextWriter BuildTextWriter { get; } = new ChannelTextWriter();
	public async Task MsBuildAsync(string solutionOrProjectFilePath, BuildType buildType = BuildType.Build, CancellationToken cancellationToken = default)
	{
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

		await BuildStarted.Invoke().ConfigureAwait(false);
		var timer = Stopwatch.StartNew();
		var buildResult = await BuildManager.DefaultBuildManager.BuildAsync(buildParameters, buildRequest, cancellationToken).ConfigureAwait(false);
		timer.Stop();
		_logger.LogInformation(buildResult.Exception, "Build result: {BuildResult} in {ElapsedMilliseconds}ms", buildResult.OverallResult, timer.ElapsedMilliseconds);
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
