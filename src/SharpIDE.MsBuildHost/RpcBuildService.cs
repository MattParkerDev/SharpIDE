using System.Net.Sockets;
using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using Microsoft.Build.Execution;
using SharpIDE.Application.Features.Build;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost;

public class RpcBuildService : IRpcBuildService
{
	private static readonly string[] RunPropertyNames = ["RunCommand", "RunArguments", "RunWorkingDirectory", "TargetPath", "OutDir"];
	private PipeTextWriter _buildTextWriter = new PipeTextWriter();
	private readonly ProjectEvaluator _projectEvaluator = new ProjectEvaluator();
	private Task? _socketWriteTask;
	public async Task<bool> Ping()
	{
		return true;
	}

	public async Task<(BuildResultDto buildResultDto, Exception? Exception)> MsBuildAsync(string solutionOrProjectFilePath, BuildTypeDto buildType = BuildTypeDto.Build, CancellationToken cancellationToken = default)
	{
		var targetsToBuild = TargetsToBuild(buildType);
		var buildRequest = new BuildRequestData(
			projectFullPath : solutionOrProjectFilePath,
			globalProperties: new Dictionary<string, string?>(),
			toolsVersion: null,
			targetsToBuild: targetsToBuild,
			hostServices: null,
			flags: BuildRequestDataFlags.None);

		var buildResult = await BuildManager.DefaultBuildManager.BuildAsync(CreateBuildParameters(), buildRequest, cancellationToken).ConfigureAwait(false);
		return (MapResult(buildResult.OverallResult), buildResult.Exception);
	}

	public async Task<RunBuildResultDto> BuildProjectForRunAsync(string projectFilePath, string? targetFramework, CancellationToken cancellationToken = default)
	{
		// Restore in its own build session, as -restore does, so the build evaluates the props/targets restore wrote
		var restoreRequest = new BuildRequestData(
			projectFullPath: projectFilePath,
			globalProperties: new Dictionary<string, string?>
			{
				["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("D"),
				["MSBuildIsRestoring"] = "true",
				["ExcludeRestorePackageImports"] = "true",
			},
			toolsVersion: null,
			targetsToBuild: ["Restore"],
			hostServices: null,
			flags: BuildRequestDataFlags.ClearCachesAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets | BuildRequestDataFlags.IgnoreMissingEmptyAndInvalidImports);
		var restoreResult = await BuildManager.DefaultBuildManager.BuildAsync(CreateBuildParameters(), restoreRequest, cancellationToken).ConfigureAwait(false);
		if (restoreResult.OverallResult is not BuildResultCode.Success)
		{
			return new RunBuildResultDto(BuildResultDto.Failure, restoreResult.Exception?.Message, []);
		}

		var globalProperties = new Dictionary<string, string?>();
		if (string.IsNullOrWhiteSpace(targetFramework) is false) globalProperties["TargetFramework"] = targetFramework;
		var buildRequest = new BuildRequestData(
			projectFullPath: projectFilePath,
			globalProperties: globalProperties,
			toolsVersion: null,
			// ComputeRunArguments only exists in newer SDKs - skipped if missing, then RunCommand/RunArguments are as evaluated
			targetsToBuild: ["Build", "ComputeRunArguments"],
			hostServices: null,
			flags: BuildRequestDataFlags.ProvideSubsetOfStateAfterBuild | BuildRequestDataFlags.SkipNonexistentTargets,
			requestedProjectState: new RequestedProjectState { PropertyFilters = [..RunPropertyNames] });
		var buildResult = await BuildManager.DefaultBuildManager.BuildAsync(CreateBuildParameters(), buildRequest, cancellationToken).ConfigureAwait(false);

		var projectState = buildResult.ProjectStateAfterBuild;
		var properties = projectState is null
			? []
			: RunPropertyNames.ToDictionary(name => name, name => projectState.GetPropertyValue(name));
		return new RunBuildResultDto(MapResult(buildResult.OverallResult), buildResult.Exception?.Message, properties);
	}

	public Task<List<ProjectEvaluationDto>> EvaluateProjectsAsync(List<string> projectFilePaths, CancellationToken cancellationToken = default)
		=> _projectEvaluator.EvaluateProjectsAsync(projectFilePaths, cancellationToken);

	public Task<List<ProjectEvaluationDto>> ReloadProjectsAsync(List<string> changedFilePaths, CancellationToken cancellationToken = default)
		=> _projectEvaluator.ReloadProjectsAsync(changedFilePaths, cancellationToken);

	public Task UnloadAllProjectsAsync() => _projectEvaluator.UnloadAllProjectsAsync();

	public Task<string> GetOrCreateUserSecretsIdAsync(string projectFilePath) => _projectEvaluator.GetOrCreateUserSecretsIdAsync(projectFilePath);

	private BuildParameters CreateBuildParameters()
	{
		var terminalLogger = InternalTerminalLoggerFactory.CreateLogger(_buildTextWriter);
		var nodesToBuildWith = GetBuildNodeCount(Environment.ProcessorCount);
		return new BuildParameters
		{
			MaxNodeCount = nodesToBuildWith,
			DisableInProcNode = false, // true makes single project builds much slower
			EnableNodeReuse = true,
			Loggers =
			[
				//new BinaryLogger { Parameters = "msbuild.binlog" },
				//new ConsoleLogger(LoggerVerbosity.Minimal) {Parameters = "FORCECONSOLECOLOR"},
				terminalLogger
			],
		};
	}

	private static BuildResultDto MapResult(BuildResultCode resultCode) => resultCode switch
	{
		BuildResultCode.Success => BuildResultDto.Success,
		BuildResultCode.Failure => BuildResultDto.Failure,
		_ => throw new ArgumentOutOfRangeException(nameof(resultCode), resultCode, null)
	};

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

