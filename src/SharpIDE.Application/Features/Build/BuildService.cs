using System.Diagnostics;
using Ardalis.GuardClauses;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Extensions.Logging;
using PolyType.SourceGenerator;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Logging;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;
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

public class BuildService(ILogger<BuildService> logger)
{
	private readonly ILogger<BuildService> _logger = logger;

	public EventWrapper<BuildStartedFlags, Task> BuildStarted { get; } = new(_ => Task.CompletedTask);
	public EventWrapper<Task> BuildFinished { get; } = new(() => Task.CompletedTask);
	public ChannelTextWriter BuildTextWriter { get; } = new ChannelTextWriter();
	private CancellationTokenSource? _cancellationTokenSource;
	private IRpcBuildService? _rpcBuildService;

	private IRpcBuildService ConnectRpc()
	{
		lock (this)
		{
			if (_rpcBuildService is not null) return _rpcBuildService;
			var sharpIdeMsBuildHostDllPath = Path.Combine(AppContext.BaseDirectory, "SharpIdeMsBuildHost", "SharpIDE.MsBuildHost.dll");
			var startupInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = sharpIdeMsBuildHostDllPath,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			var process = Process.Start(startupInfo);
			if (process is null) throw new InvalidOperationException("Failed to start SharpIDE.MsBuildHost");
			var handler = new LengthHeaderMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new NerdbankMessagePackFormatter { TypeShapeProvider = TypeShapeProvider_SharpIDE_MsBuildHost_Contracts.Default });
			var rpc = new JsonRpc(handler);

			rpc.StartListening();

			var proxy = rpc.Attach<IRpcBuildService>();
			return proxy;
		}
	}
	public async Task<SharpIdeBuildResult> MsBuildAsync(string solutionOrProjectFilePath, BuildType buildType = BuildType.Build, BuildStartedFlags buildStartedFlags = BuildStartedFlags.UserFacing, CancellationToken cancellationToken = default)
	{
		_rpcBuildService ??= ConnectRpc();
		if (_cancellationTokenSource is not null) throw new InvalidOperationException("A build is already in progress.");
		_cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(BuildService)}.{nameof(MsBuildAsync)}");

		BuildStarted.InvokeParallelFireAndForget(buildStartedFlags);
		var timer = Stopwatch.StartNew();
		var buildTypeDto = buildType switch
		{
			BuildType.Build => BuildTypeDto.Build,
			BuildType.Rebuild => BuildTypeDto.Rebuild,
			BuildType.Clean => BuildTypeDto.Clean,
			BuildType.Restore => BuildTypeDto.Restore,
			_ => throw new ArgumentOutOfRangeException(nameof(buildType), buildType, null)
		};
		var (buildResult, exception) = await _rpcBuildService.MsBuildAsync(solutionOrProjectFilePath, buildTypeDto, _cancellationTokenSource.Token).ConfigureAwait(false);
		timer.Stop();
		BuildFinished.InvokeParallelFireAndForget();
		_cancellationTokenSource = null;
		_logger.LogInformation(exception, "Build result: {BuildResult} in {ElapsedMilliseconds}ms", buildResult, timer.ElapsedMilliseconds);
		var mappedResult = buildResult switch
		{
			BuildResultDto.Success => SharpIdeBuildResult.Success,
			BuildResultDto.Failure => SharpIdeBuildResult.Failure,
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
}
