using System.Net.Sockets;
using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using Microsoft.Build.Execution;
using SharpIDE.Application.Features.Build;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost;

public class RpcBuildService : IRpcBuildService
{
	private PipeTextWriter _buildTextWriter = new PipeTextWriter();
	private Task? _socketWriteTask;
	public async Task<bool> Ping()
	{
		return true;
	}

	public async Task<(BuildResultDto buildResultDto, Exception? Exception)> MsBuildAsync(string solutionOrProjectFilePath, BuildTypeDto buildType = BuildTypeDto.Build, CancellationToken cancellationToken = default)
	{
		var terminalLogger = InternalTerminalLoggerFactory.CreateLogger(_buildTextWriter);

		var nodesToBuildWith = GetBuildNodeCount(Environment.ProcessorCount);
		var buildParameters = new BuildParameters
		{
			MaxNodeCount = nodesToBuildWith,
			DisableInProcNode = false,
			EnableNodeReuse = true,
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

	public async Task BeginWritingMsBuildOutputToSocket(string unixDomainSocketFilePath)
	{
		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		var endpoint = new UnixDomainSocketEndPoint(unixDomainSocketFilePath);
		await socket.ConnectAsync(endpoint);
		if (_socketWriteTask is not null) throw new InvalidOperationException("A socket write task is already running.");
		_socketWriteTask = Task.Run(async () =>
		{
			var pipeReader = _buildTextWriter.Reader;
			while (true)
			{
				var result = await pipeReader.ReadAsync();
				var buffer = result.Buffer;

				foreach (var segment in buffer)
				{
					await socket.SendAsync(segment, SocketFlags.None);
				}

				pipeReader.AdvanceTo(buffer.End);

				if (result.IsCompleted) break;
			}

			await pipeReader.CompleteAsync();
			socket.Shutdown(SocketShutdown.Send);
		});
	}

	public async Task<(string RuntimeVersion, string MsBuildSdkPath)> GetMsbuildInfoAsync()
	{
		return (RuntimeInformation.FrameworkDescription, SharpIdeMsbuildLocator.ResolvedMsBuildSdkPath);
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

