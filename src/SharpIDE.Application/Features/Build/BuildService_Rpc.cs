using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NeoSmart.AsyncLock;
using NuGet.Versioning;
using PolyType.SourceGenerator;
using SharpIDE.Application.Features.Events;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Build;

/// One SharpIDE.MsBuildHost process per opened solution, serving real builds (evaluation is served by DesignTimeBuildService's host).
/// It uses the SDK resolved from the solution's folder (global.json), so it is started when the solution is opened, and killed when this (scoped) service is disposed - i.e. when another solution is opened.
/// If it crashes, it is restarted.
public partial class BuildService
{
	private Process? _sharpIdeMsBuildHostProcess;
	private JsonRpc? _rpc;
	private Socket? _buildLogSocket;
	private Task? _fillPipeFromLoggerTask;
	private readonly AsyncLock _rpcInitLock = new AsyncLock();
	private volatile bool _rpcDisposing;
	private volatile string? _hostSolutionFilePath;
	private int _consecutiveCrashCount;
	private const int MaxConsecutiveCrashRestarts = 3;

	/// Starts the host for the solution being opened, so it isn't left until the first build (the restore before the workspace loads).
	/// The solution path is recorded synchronously, so callers that don't await this can still use the host as soon as it is up.
	public Task StartHostAsync(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		if (_hostSolutionFilePath is not null && _hostSolutionFilePath != solutionFilePath) throw new InvalidOperationException("The build host is already started for a different solution. A new solution gets a new BuildService scope.");
		_hostSolutionFilePath = solutionFilePath;
		return GetRpcAsync(cancellationToken);
	}

	internal async Task<IRpcBuildService> GetRpcAsync(CancellationToken cancellationToken = default)
	{
		using (await _rpcInitLock.LockAsync(cancellationToken))
		{
			ObjectDisposedException.ThrowIf(_rpcDisposing, this);
			if (_rpcBuildService is not null) return _rpcBuildService;
			var solutionFilePath = _hostSolutionFilePath ?? throw new InvalidOperationException($"The build host has not been started - call {nameof(StartHostAsync)} when the solution is opened.");
			_rpcBuildService = await ConnectRpc(solutionFilePath);
			return _rpcBuildService;
		}
	}

	private async Task<IRpcBuildService> ConnectRpc(string solutionOrProjectFilePath)
	{
		if (_fillPipeFromLoggerTask is not null) throw new InvalidOperationException("Build logger pipe is already open, but RPC service is not initialized. This should never happen.");
		var (process, rpc) = await MsBuildHostProcess.StartAsync(solutionOrProjectFilePath, designTime: false, _logger, OnRpcDisconnected);

		var proxy = rpc.Attach<IRpcBuildService>();
		var (rpcBuildHostRuntimeVersion, rpcBuildHostMsBuildPath) = await proxy.GetMsbuildInfoAsync();
		_logger.LogInformation("Connected to SharpIDE.MsBuildHost running on '{RpcBuildHostRuntimeVersion}' Runtime with MSBuild from SDK at '{RpcBuildHostMsBuildPath}'", rpcBuildHostRuntimeVersion, rpcBuildHostMsBuildPath);
		_fillPipeFromLoggerTask = await OpenMsBuildLoggerPipe(proxy);
		_sharpIdeMsBuildHostProcess = process;
		_rpc = rpc;
		return proxy;
	}

	private void OnRpcDisconnected(JsonRpc rpc, JsonRpcDisconnectedEventArgs e)
	{
		if (_rpcDisposing) return;
		_ = Task.Run(async () =>
		{
			using (await _rpcInitLock.LockAsync())
			{
				if (_rpcDisposing || ReferenceEquals(_rpc, rpc) is false) return; // Already handled, or a connection that never completed
				_logger.LogError(e.Exception, "SharpIDE.MsBuildHost disconnected ({Reason}: {Description}), restarting it", e.Reason, e.Description);
				TearDownConnection();
			}

			if (++_consecutiveCrashCount > MaxConsecutiveCrashRestarts)
			{
				_logger.LogError("SharpIDE.MsBuildHost has crashed {CrashCount} times in a row - not restarting it until it is next needed", _consecutiveCrashCount);
				return;
			}
			try
			{
				await GetRpcAsync();
				_consecutiveCrashCount = 0;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to restart SharpIDE.MsBuildHost");
			}
		});
	}

	/// Must be called inside _rpcInitLock
	private void TearDownConnection()
	{
		_rpcBuildService = null;
		_rpc?.Dispose();
		_rpc = null;
		try { _buildLogSocket?.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
		_buildLogSocket?.Close(0);
		_buildLogSocket = null;
		_fillPipeFromLoggerTask = null;
		try { _sharpIdeMsBuildHostProcess?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // Already exited
		_sharpIdeMsBuildHostProcess?.Dispose();
		_sharpIdeMsBuildHostProcess = null;
	}

	private async Task<Task> OpenMsBuildLoggerPipe(IRpcBuildService rpcBuildService)
	{
		if (_buildLogSocket is not null) throw new InvalidOperationException("Build log socket is already open.");
		var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
		var pipeReader = pipe.Reader;
		var pipeWriter = pipe.Writer;

		var unixDomainSocketFilePath = $"{Path.GetTempFileName()}.sock";
		var serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		serverSocket.Bind(new UnixDomainSocketEndPoint(unixDomainSocketFilePath));
		serverSocket.Listen(1);
		await rpcBuildService.BeginWritingMsBuildOutputToSocket(unixDomainSocketFilePath);

		var socket = await serverSocket.AcceptAsync();
		serverSocket.Close();
		File.Delete(unixDomainSocketFilePath);
		var fillPipeTask = Task.Run(async () =>
		{
			try
			{
				var buffer = new byte[4096];
				while (true)
				{
					if (_rpcDisposing) break;
					var bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None);
					if (bytesRead == 0) break; // End of stream
					await pipeWriter.WriteAsync(buffer.AsMemory(0, bytesRead));
				}
			}
			catch when (_rpcDisposing || ReferenceEquals(_buildLogSocket, socket) is false)
			{
				// Socket is being closed, or the host crashed and was torn down
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error while reading from SharpIDE.MsBuildHost logger socket");
			}
			finally
			{
				await pipeWriter.CompleteAsync();
			}
		});
		BuildLogPipeReader = pipeReader;
		_buildLogSocket = socket;
		return fillPipeTask;
	}

	public void Dispose()
	{
		_rpcDisposing = true;
		_cancellationTokenSource?.Cancel();
		TearDownConnection();
	}
}
