using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NeoSmart.AsyncLock;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Build;

/// The design-time build host (SharpIDE.MsBuildHost --design-time) - one per opened solution, alongside BuildService's host, which runs the user's builds.
/// Long-lived, so its MSBuild (and the XML it has read) stays warm between loads. Started on first use. Killed when this (scoped) service is disposed.
/// If it crashes, the next call starts a new one - every load asks for all the design-time builds it needs, so there is nothing to restore.
public sealed class DesignTimeBuildService(ILogger<DesignTimeBuildService> logger) : IDisposable
{
	private readonly ILogger<DesignTimeBuildService> _logger = logger;
	private readonly AsyncLock _rpcInitLock = new();
	private Process? _process;
	private JsonRpc? _rpc;
	private IRpcDesignTimeBuildService? _rpcService;
	private volatile bool _disposing;

	/// Starts the host, if it isn't already, so it can be warming up meanwhile (e.g. during the restore before a load)
	public Task StartHostAsync(string solutionFilePath, CancellationToken cancellationToken = default) => GetRpcAsync(solutionFilePath, cancellationToken);

	public async Task<List<DesignTimeBuildResultDto>> DesignTimeBuildAsync(string solutionFilePath, List<string> projectFilePaths, Dictionary<string, string> globalProperties, CancellationToken cancellationToken = default)
	{
		var rpc = await GetRpcAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);
		var timer = Stopwatch.StartNew();
		var results = await rpc.DesignTimeBuildAsync(projectFilePaths, globalProperties, cancellationToken).ConfigureAwait(false);
		_logger.LogInformation("Design-time built {ProjectCount} projects in {ElapsedMilliseconds}ms", projectFilePaths.Count, timer.ElapsedMilliseconds);
		return results;
	}

	public async Task<string?> TryGetProjectOutputPathAsync(string solutionFilePath, string projectFilePath, CancellationToken cancellationToken = default)
	{
		var rpc = await GetRpcAsync(solutionFilePath, cancellationToken).ConfigureAwait(false);
		return await rpc.TryGetProjectOutputPathAsync(projectFilePath, cancellationToken).ConfigureAwait(false);
	}

	private async Task<IRpcDesignTimeBuildService> GetRpcAsync(string solutionFilePath, CancellationToken cancellationToken)
	{
		using (await _rpcInitLock.LockAsync(cancellationToken))
		{
			ObjectDisposedException.ThrowIf(_disposing, this);
			if (_rpcService is not null) return _rpcService;

			var (process, rpc) = await MsBuildHostProcess.StartAsync(solutionFilePath, designTime: true, _logger, OnRpcDisconnected);
			_process = process;
			_rpc = rpc;
			var rpcService = rpc.Attach<IRpcDesignTimeBuildService>();
			var (runtimeVersion, msBuildSdkPath) = await rpcService.GetMsbuildInfoAsync();
			_logger.LogInformation("Connected to SharpIDE.MsBuildHost (design-time) running on '{RuntimeVersion}' Runtime with MSBuild from SDK at '{MsBuildPath}'", runtimeVersion, msBuildSdkPath);
			_rpcService = rpcService;
			return rpcService;
		}
	}

	private void OnRpcDisconnected(JsonRpc rpc, JsonRpcDisconnectedEventArgs e)
	{
		if (_disposing) return;
		_ = Task.Run(async () =>
		{
			using (await _rpcInitLock.LockAsync())
			{
				if (_disposing || ReferenceEquals(_rpc, rpc) is false) return; // Already handled, or a connection that never completed
				_logger.LogError(e.Exception, "SharpIDE.MsBuildHost (design-time) disconnected ({Reason}: {Description}) - a new one is started when next needed", e.Reason, e.Description);
				TearDown();
			}
		});
	}

	/// Must be called inside _rpcInitLock (or when disposing)
	private void TearDown()
	{
		_rpcService = null;
		_rpc?.Dispose();
		_rpc = null;
		try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // Already exited
		_process?.Dispose();
		_process = null;
	}

	public void Dispose()
	{
		_disposing = true;
		TearDown();
	}
}
