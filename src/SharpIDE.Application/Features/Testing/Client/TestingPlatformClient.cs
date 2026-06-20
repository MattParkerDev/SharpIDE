using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using SharpIDE.Application.Features.Testing.Client.Dtos;
using StreamJsonRpc;
using AttachDebuggerInfo = SharpIDE.Application.Features.Testing.Client.Dtos.AttachDebuggerInfo;
using ClientCapabilities = SharpIDE.Application.Features.Testing.Client.Dtos.ClientCapabilities;
using ClientInfo = SharpIDE.Application.Features.Testing.Client.Dtos.ClientInfo;

namespace SharpIDE.Application.Features.Testing.Client;

public sealed class TestingPlatformClient : IAsyncDisposable
{
    private readonly TcpClient _tcpClient = new();
    private readonly IProcessHandle _processHandler;
    private readonly TargetHandler _targetHandler = new();
    private readonly StringBuilder _disconnectionReason = new();

    public TestingPlatformClient(JsonRpc jsonRpc, TcpClient tcpClient, IProcessHandle processHandler, bool enableDiagnostic = false)
    {
        JsonRpcClient = jsonRpc;
        _tcpClient = tcpClient;
        _processHandler = processHandler;
        JsonRpcClient.AddLocalRpcTarget(
            _targetHandler,
            new JsonRpcTargetOptions
            {
                MethodNameTransform = CommonMethodNameTransforms.CamelCase,
            });

        if (enableDiagnostic)
        {
            JsonRpcClient.TraceSource.Switch.Level = SourceLevels.All;
            JsonRpcClient.TraceSource.Listeners.Add(new ConsoleRpcListener());
        }

        JsonRpcClient.Disconnected += JsonRpcClient_Disconnected;
        JsonRpcClient.StartListening();
    }

    private void JsonRpcClient_Disconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        _disconnectionReason.AppendLine("Disconnected reason:");
        _disconnectionReason.AppendLine(e.Reason.ToString());
        _disconnectionReason.AppendLine(e.Description);
        _disconnectionReason.AppendLine(e.Exception?.ToString());
    }

    public int ExitCode => _processHandler.ExitCode;

    public async Task<int> WaitServerProcessExitAsync()
    {
        await _processHandler.WaitForExitAsync();
        return _processHandler.ExitCode;
    }

    public JsonRpc JsonRpcClient { get; }

    private async Task CheckedInvokeAsync(Func<Task> func)
    {
        try
        {
            await func();
        }
        catch (Exception ex) when (_disconnectionReason.Length > 0)
        {
            throw new InvalidOperationException($"{ex.Message}\n{_disconnectionReason}", ex);
        }
    }

    private async Task<T> CheckedInvokeAsync<T>(Func<Task<T>> func, bool @checked = true)
    {
        try
        {
            return await func();
        }
        catch (Exception ex)
        {
            if (@checked)
            {
                if (_disconnectionReason.Length > 0)
                {
                    throw new InvalidOperationException($"{ex.Message}\n{_disconnectionReason}", ex);
                }

                throw;
            }
        }

        return default!;
    }

    public void RegisterLogListener(LogsCollector listener)
        => _targetHandler.RegisterLogListener(listener);

    public void RegisterTelemetryListener(TelemetryCollector listener)
        => _targetHandler.RegisterTelemetryListener(listener);

    public async Task<InitializeResponse> InitializeAsync()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(3));
        return await CheckedInvokeAsync(async () => await JsonRpcClient.InvokeWithParameterObjectAsync<InitializeResponse>(
            "initialize",
            new InitializeRequest(Environment.ProcessId, new ClientInfo("test-client"),
                new ClientCapabilities(new ClientTestingCapabilities(DebuggerProvider: false))), cancellationToken: cancellationTokenSource.Token));
    }

    public async Task ExitAsync(bool gracefully = true)
    {
        if (gracefully)
        {
            using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(3));
            await CheckedInvokeAsync(async () => await JsonRpcClient.NotifyWithParameterObjectAsync("exit", new object()));
        }
        else
        {
            _tcpClient.Dispose();
        }
    }

    public async Task<ResponseListener> DiscoverTestsAsync(Guid requestId, Func<TestNodeUpdate[], Task> action, bool @checked = true)
        => await CheckedInvokeAsync(
            async () =>
            {
                using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(3));
                var discoveryListener = new TestNodeUpdatesResponseListener(requestId, action);
                _targetHandler.RegisterResponseListener(discoveryListener);
                await JsonRpcClient.InvokeWithParameterObjectAsync("testing/discoverTests", new DiscoveryRequest(RunId: requestId), cancellationToken: cancellationTokenSource.Token);
                return discoveryListener;
            }, @checked);

    public async Task<ResponseListener> RunTestsAsync(Guid requestId, Func<TestNodeUpdate[], Task> action)
        => await CheckedInvokeAsync(async () =>
        {
            using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(3));
            var runListener = new TestNodeUpdatesResponseListener(requestId, action);
            _targetHandler.RegisterResponseListener(runListener);
            await JsonRpcClient.InvokeWithParameterObjectAsync("testing/runTests", new RunTestsRequest(RunId: requestId, Tests: null), cancellationToken: cancellationTokenSource.Token);
            return runListener;
        });

    public async Task<ResponseListener> RunTestsAsync(Guid requestId, TestNode[] filter, Func<TestNodeUpdate[], Task> action)
    => await CheckedInvokeAsync(async () =>
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(3));
        var runListener = new TestNodeUpdatesResponseListener(requestId, action);
        _targetHandler.RegisterResponseListener(runListener);
        await JsonRpcClient.InvokeWithParameterObjectAsync("testing/runTests", new RunTestsRequest(Tests: filter, RunId: requestId), cancellationToken: cancellationTokenSource.Token);
        return runListener;
    });

    private int _disposed = 0;
    public async ValueTask DisposeAsync()
    {
	    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
	    await ExitAsync();
	    JsonRpcClient.Dispose();
	    _tcpClient.Dispose();
	    await _processHandler.WaitForExitAsync();
	    _processHandler.Dispose();
    }

    public record Log(LogLevel LogLevel, string Message);

    private sealed class TargetHandler
    {
        private readonly ConcurrentDictionary<Guid, ResponseListener> _listeners
            = new();

        private readonly ConcurrentBag<LogsCollector> _logListeners = [];

        private readonly ConcurrentBag<TelemetryCollector> _telemetryPayloads = [];

        public void RegisterTelemetryListener(TelemetryCollector listener)
            => _telemetryPayloads.Add(listener);

        public void RegisterLogListener(LogsCollector listener)
            => _logListeners.Add(listener);

        public void RegisterResponseListener(ResponseListener responseListener)
            => _ = _listeners.TryAdd(responseListener.RequestId, responseListener);

        [JsonRpcMethod("client/attachDebugger", UseSingleObjectParameterDeserialization = true)]
        public static Task AttachDebuggerAsync(AttachDebuggerInfo attachDebuggerInfo) => throw new NotImplementedException();

        [JsonRpcMethod("testing/testUpdates/tests")]
        public async Task TestsUpdateAsync(Guid runId, TestNodeUpdate[]? changes)
        {
            if (_listeners.TryGetValue(runId, out ResponseListener? responseListener))
            {
                if (changes is null)
                {
                    responseListener.Complete();
                    _listeners.TryRemove(runId, out _);
                }
                else
                {
                    await responseListener.OnMessageReceiveAsync(changes);
                }
            }
        }

        [JsonRpcMethod("telemetry/update", UseSingleObjectParameterDeserialization = true)]
        public Task TelemetryAsync(TelemetryPayload telemetry)
        {
            foreach (TelemetryCollector listener in _telemetryPayloads)
            {
                listener.Add(telemetry);
            }

            return Task.CompletedTask;
        }

        [JsonRpcMethod("client/log")]
        public Task LogAsync(LogLevel level, string message)
        {
            foreach (LogsCollector listener in _logListeners)
            {
                listener.Add(new(level, message));
            }

            return Task.CompletedTask;
        }
    }
}

public abstract class ResponseListener
{
    private readonly TaskCompletionSource _allMessageReceived = new();

    public Guid RequestId { get; set; }

    protected ResponseListener(Guid requestId) => RequestId = requestId;

    public abstract Task OnMessageReceiveAsync(object message);

    internal void Complete() => _allMessageReceived.SetResult();

    public Task WaitCompletionAsync() => _allMessageReceived.Task;
}

public sealed class TestNodeUpdatesResponseListener : ResponseListener
{
    private readonly Func<TestNodeUpdate[], Task> _action;

    public TestNodeUpdatesResponseListener(Guid requestId, Func<TestNodeUpdate[], Task> action)
    : base(requestId) => _action = action;

    public override async Task OnMessageReceiveAsync(object message)
        => await _action((TestNodeUpdate[])message);
}
