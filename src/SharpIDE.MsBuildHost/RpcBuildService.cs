using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.Build.Execution;
using SharpIDE.Application.Features.Build;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

namespace SharpIDE.MsBuildHost;

public class RpcBuildService : IRpcBuildService
{
	public ChannelTextWriter BuildTextWriter { get; } = new ChannelTextWriter();
	public async Task<bool> Ping()
	{
		return true;
	}

	public async Task<(BuildResultDto buildResultDto, Exception? Exception)> MsBuildAsync(string solutionOrProjectFilePath, BuildTypeDto buildType = BuildTypeDto.Build, CancellationToken cancellationToken = default)
	{
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

		var buildResult = await BuildManager.DefaultBuildManager.BuildAsync(buildParameters, buildRequest, cancellationToken).ConfigureAwait(false);
		var mappedResult = buildResult.OverallResult switch
		{
			BuildResultCode.Success => BuildResultDto.Success,
			BuildResultCode.Failure => BuildResultDto.Failure,
			_ => throw new ArgumentOutOfRangeException()
		};
		return (mappedResult, buildResult.Exception);
	}

	private static string[] TargetsToBuild(BuildTypeDto buildType)
	{
		string[] targetsToBuild = buildType switch
		{
			BuildTypeDto.Build => ["Restore", "Build"],
			BuildTypeDto.Rebuild => ["Restore", "Rebuild"],
			BuildTypeDto.Clean => ["Clean"],
			BuildTypeDto.Restore => ["Restore"],
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
		Guard.Against.NegativeOrZero(nodesToBuildWith);
		return nodesToBuildWith;
	}
}

