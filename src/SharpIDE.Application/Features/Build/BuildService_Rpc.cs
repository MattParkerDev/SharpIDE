using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NeoSmart.AsyncLock;
using NuGet.Versioning;
using PolyType.SourceGenerator;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Build;

public partial class BuildService
{
    private Task? _fillPipeFromLoggerTask;
    private Process? _sharpIdeMsBuildHostProcess;
    private Socket? _buildLogSocket;
	private readonly AsyncLock _rpcInitLock = new AsyncLock();

    private async Task<IRpcBuildService> ConnectRpc(string solutionOrProjectFilePath)
    {
        using (await _rpcInitLock.LockAsync())
        {
            if (_rpcBuildService is not null) return _rpcBuildService;
            if (_fillPipeFromLoggerTask is not null) throw new InvalidOperationException("Build logger pipe is already open, but RPC service is not initialized. This should never happen.");
            var sharpIdeMsBuildHostDllPath = Path.Combine(AppContext.BaseDirectory, "SharpIdeMsBuildHost", "SharpIDE.MsBuildHost.dll");
            var sdkVersion = await GetCorrectDotnetSdkVersion(solutionOrProjectFilePath);
            var startupInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { sharpIdeMsBuildHostDllPath, sdkVersion.ToNormalizedString() },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var keysToRemove = startupInfo.Environment
                .Where(s => s.Key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || s.Key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase))
                .Where(s => s.Key.Equals("DOTNET_CLI_TELEMETRY_OPTOUT", StringComparison.OrdinalIgnoreCase) is false) // I don't know if this affects anything in MSBuild, but let's propagate it anyway if the user has it set
                .Select(s => s.Key)
                .ToList();
            keysToRemove.ForEach(s => startupInfo.Environment.Remove(s));
            startupInfo.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
            // ["DOTNET_ROLL_FORWARD"] = "LatestMajor" isn't necessary as it is set in the SharpIDE.MsBuildHost.runtimeconfig.json, from the <RollForward>LatestMajor</RollForward> csproj property
            var process = Process.Start(startupInfo);
            if (process is null) throw new InvalidOperationException("Failed to start SharpIDE.MsBuildHost");
            var handler = new LengthHeaderMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new NerdbankMessagePackFormatter { TypeShapeProvider = TypeShapeProvider_SharpIDE_MsBuildHost_Contracts.Default });
            var rpc = new JsonRpc(handler);

            rpc.StartListening();

            var proxy = rpc.Attach<IRpcBuildService>();
            var (rpcBuildHostRuntimeVersion, rpcBuildHostMsBuildPath) = await proxy.GetMsbuildInfoAsync();
            _logger.LogInformation("Connected to SharpIDE.MsBuildHost running on '{RpcBuildHostRuntimeVersion}' Runtime with MSBuild from SDK at '{RpcBuildHostMsBuildPath}'", rpcBuildHostRuntimeVersion, rpcBuildHostMsBuildPath);
            _fillPipeFromLoggerTask = await OpenMsBuildLoggerPipe(proxy);
            _sharpIdeMsBuildHostProcess = process;
            return proxy;
        }
    }

    private static async Task<NuGetVersion> GetCorrectDotnetSdkVersion(string solutionOrProjectFilePath)
    {
	    var workingDirectory = Path.GetDirectoryName(solutionOrProjectFilePath) ?? throw new InvalidOperationException("Could not determine working directory from solution or project file path.");
	    var result = await Cli.Wrap("dotnet").WithWorkingDirectory(workingDirectory).WithArguments("--version").ExecuteBufferedAsync();
	    var sdkVersion = NuGetVersion.Parse(result.StandardOutput.Trim());
	    return sdkVersion;
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
                    var bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None);
                    if (bytesRead == 0) break; // End of stream
                    await pipeWriter.WriteAsync(buffer.AsMemory(0, bytesRead));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while reading from SharpIDE.MsBuildHost logger socket");
            }
            finally
            {
                await pipeWriter.CompleteAsync();
                socket.Dispose();
            }
        });
        BuildLogPipeReader = pipeReader;
        _buildLogSocket = socket;
        return fillPipeTask;
    }
}
