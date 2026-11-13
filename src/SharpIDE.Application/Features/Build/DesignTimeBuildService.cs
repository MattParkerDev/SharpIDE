using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NeoSmart.AsyncLock;
using SharpIDE.Application.Features.Events;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Build;

/// The design-time build host (SharpIDE.MsBuildHost --design-time) - one per opened solution, alongside BuildService's host, which runs the user's builds.
/// Serves project evaluation and design-time builds, which share its parsed XML.
/// Long-lived, so its MSBuild (and the XML it has read) stays warm between loads. Started when the solution is opened. Killed when this (scoped) service is disposed.
/// If it crashes, it is restarted, and HostRestarted is raised so everything can be re-evaluated - every load asks for all the design-time builds it needs, so those need nothing restored.
public sealed class DesignTimeBuildService(ILogger<DesignTimeBuildService> logger) : IDisposable
{
	private readonly ILogger<DesignTimeBuildService> _logger = logger;
	private readonly AsyncLock _rpcInitLock = new();
	private Process? _process;
	private JsonRpc? _rpc;
	private IRpcDesignTimeBuildService? _rpcService;
	private volatile bool _disposing;
	private volatile string? _hostSolutionFilePath;
	private int _consecutiveCrashCount;
	private const int MaxConsecutiveCrashRestarts = 3;

	/// Raised once the host has been restarted after a crash. The new host has no evaluations, so every project needs re-evaluating.
	public EventWrapper<Task> HostRestarted { get; } = new(() => Task.CompletedTask);

	/// Starts the host, if it isn't already, so it can be warming up meanwhile (e.g. during the restore before a load). Evaluation needs it as soon as the solution is opened.
	/// The solution path is recorded synchronously, so callers that don't await this can still use the host as soon as it is up.
	public Task StartHostAsync(string solutionFilePath, CancellationToken cancellationToken = default)
	{
		if (_hostSolutionFilePath is not null && _hostSolutionFilePath != solutionFilePath) throw new InvalidOperationException("The design-time build host is already started for a different solution. A new solution gets a new DesignTimeBuildService scope.");
		_hostSolutionFilePath = solutionFilePath;
		return GetRpcAsync(cancellationToken);
	}

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

	private Task<IRpcDesignTimeBuildService> GetRpcAsync(string solutionFilePath, CancellationToken cancellationToken)
	{
		_hostSolutionFilePath ??= solutionFilePath;
		return GetRpcAsync(cancellationToken);
	}

	internal async Task<IRpcDesignTimeBuildService> GetRpcAsync(CancellationToken cancellationToken = default)
	{
		using (await _rpcInitLock.LockAsync(cancellationToken))
		{
			ObjectDisposedException.ThrowIf(_disposing, this);
			if (_rpcService is not null) return _rpcService;
			var solutionFilePath = _hostSolutionFilePath ?? throw new InvalidOperationException($"The design-time build host has not been started - call {nameof(StartHostAsync)} when the solution is opened.");

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
				_logger.LogError(e.Exception, "SharpIDE.MsBuildHost (design-time) disconnected ({Reason}: {Description}), restarting it", e.Reason, e.Description);
				TearDown();
			}

			if (++_consecutiveCrashCount > MaxConsecutiveCrashRestarts)
			{
				_logger.LogError("SharpIDE.MsBuildHost (design-time) has crashed {CrashCount} times in a row - not restarting it until it is next needed", _consecutiveCrashCount);
				return;
			}
			try
			{
				await GetRpcAsync();
				await HostRestarted.InvokeParallelAsync();
				_consecutiveCrashCount = 0;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to restart SharpIDE.MsBuildHost (design-time)");
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
