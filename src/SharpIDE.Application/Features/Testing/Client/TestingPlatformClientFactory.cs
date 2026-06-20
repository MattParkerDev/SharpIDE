using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.Testing.Client;

public partial /* for codegen regx */ class TestingPlatformClientFactory
{
    //private static readonly string Root = RootFinder.Find();
    private static readonly Dictionary<string, string> DefaultEnvironmentVariables = new()
    {
        //{ "DOTNET_ROOT", $"{Root}/.dotnet" },
        //{ "DOTNET_INSTALL_DIR", $"{Root}/.dotnet" },
        { "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1" },
        { "DOTNET_MULTILEVEL_LOOKUP", "0" },
    };

    public static async Task<TestingPlatformClient> StartAsServerAndConnectToTheClientAsync(string testApp)
    {
        var environmentVariables = new Dictionary<string, string>(DefaultEnvironmentVariables);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            // Skip all unwanted environment variables.
            string? key = entry.Key.ToString();
            if (WellKnownEnvironmentVariables.ToSkipEnvironmentVariables.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            environmentVariables[key!] = entry.Value!.ToString()!;
        }

        // We expect to not fail for unhandled exception in server mode for IDE needs.
        environmentVariables.Add("TESTINGPLATFORM_EXIT_PROCESS_ON_UNHANDLED_EXCEPTION", "0");

        // To attach to the server on startup
        // environmentVariables.Add(EnvironmentVariableConstants.TESTINGPLATFORM_LAUNCH_ATTACH_DEBUGGER, "1");
        TcpListener tcpListener = new(IPAddress.Loopback, 0);
        tcpListener.Start();
        StringBuilder builder = new();
        ProcessConfiguration processConfig = new(testApp)
        {
            OnStandardOutput = (_, output) => builder.AppendLine(CultureInfo.InvariantCulture, $"OnStandardOutput:\n{output}"),
            OnErrorOutput = (_, output) => builder.AppendLine(CultureInfo.InvariantCulture, $"OnErrorOutput:\n{output}"),
            OnExit = (_, exitCode) => builder.AppendLine(CultureInfo.InvariantCulture, $"OnExit: exit code '{exitCode}'"),

            Arguments = $"--server --client-host localhost --client-port {((IPEndPoint)tcpListener.LocalEndpoint).Port}",
            // Arguments = $"--server --client-host localhost --client-port {((IPEndPoint)tcpListener.LocalEndpoint).Port} --diagnostic --diagnostic-verbosity trace",
            EnvironmentVariables = environmentVariables,
        };

        IProcessHandle processHandler = ProcessFactory.Start(processConfig, cleanDefaultEnvironmentVariableIfCustomAreProvided: false);

        TcpClient? tcpClient;
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(60));
        try
        {
            tcpClient = await tcpListener.AcceptTcpClientAsync(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationTokenSource.Token)
        {
            throw new OperationCanceledException($"Timeout on connection for command line '{processConfig.FileName} {processConfig.Arguments}'\n{builder}", ex, cancellationTokenSource.Token);
        }
        var tcpStream = tcpClient.GetStream();
	    var handler = new HeaderDelimitedMessageHandler(tcpStream, tcpStream, new SystemTextJsonFormatter { JsonSerializerOptions = RpcJsonSerializerOptions.Default });
        return new TestingPlatformClient(new JsonRpc(handler), tcpClient, processHandler);
    }
}

public sealed class ProcessConfiguration
{
    public ProcessConfiguration(string fileName) => FileName = fileName;

    public string FileName { get; }

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public IDictionary<string, string>? EnvironmentVariables { get; init; }

    public Action<IProcessHandle, string>? OnErrorOutput { get; init; }

    public Action<IProcessHandle, string>? OnStandardOutput { get; init; }

    public Action<IProcessHandle, int>? OnExit { get; init; }
}

public interface IProcessHandle
{
    int Id { get; }
    string ProcessName { get; }
    int ExitCode { get; }
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }
    void Dispose();
    void Kill();
    Task<int> StopAsync();
    Task<int> WaitForExitAsync();
    void WaitForExit();
    Task WriteInputAsync(string input);
}

public static class ProcessFactory
{
    public static IProcessHandle Start(ProcessConfiguration config, bool cleanDefaultEnvironmentVariableIfCustomAreProvided = false)
    {
        string fullPath = config.FileName; // Path.GetFullPath(startInfo.FileName);
        string workingDirectory = config.WorkingDirectory
            .OrDefault(Path.GetDirectoryName(config.FileName).OrDefault(Directory.GetCurrentDirectory()));

        ProcessStartInfo processStartInfo = new()
        {
            FileName = fullPath,
            Arguments = config.Arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        if (config.EnvironmentVariables is not null)
        {
            if (cleanDefaultEnvironmentVariableIfCustomAreProvided)
            {
                processStartInfo.Environment.Clear();
                processStartInfo.EnvironmentVariables.Clear();
            }

            foreach (KeyValuePair<string, string> kvp in config.EnvironmentVariables)
            {
                if (kvp.Value is null)
                {
                    continue;
                }

                processStartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        Process process = new()
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = true,
        };

        // ToolName and Pid are not populated until we start the process,
        // and once we stop the process we cannot retrieve the info anymore
        // so we start the process, try to grab the needed info and set it.
        // And then we give the call reference to ProcessHandle, but not to ProcessHandleInfo
        // so they can easily get the info, but cannot change it.
        ProcessHandleInfo processHandleInfo = new();
        ProcessHandle processHandle = new(process, processHandleInfo);

        if (config.OnExit != null)
        {
            process.Exited += (_, _) => config.OnExit.Invoke(processHandle, process.ExitCode);
        }

        if (config.OnStandardOutput != null)
        {
            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    config.OnStandardOutput(processHandle, e.Data);
                }
            };
        }

        if (config.OnErrorOutput != null)
        {
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    config.OnErrorOutput(processHandle, e.Data);
                }
            };
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Process failed to start");
        }

        try
        {
            processHandleInfo.ProcessName = process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            // The associated process has exited.
            // https://learn.microsoft.com/dotnet/api/system.diagnostics.process.processname?view=net-7.0
        }

        processHandleInfo.Id = process.Id;

        if (config.OnStandardOutput != null)
        {
            process.BeginOutputReadLine();
        }

        if (config.OnErrorOutput != null)
        {
            process.BeginErrorReadLine();
        }

        return processHandle;
    }
}

public sealed class ProcessHandleInfo
{
    public string? ProcessName { get; internal set; }

    public int Id { get; internal set; }
}

public sealed class ProcessHandle : IProcessHandle, IDisposable
{
    private readonly ProcessHandleInfo _processHandleInfo;
    private readonly Process _process;
    private bool _disposed;
    private int _exitCode;

    internal ProcessHandle(Process process, ProcessHandleInfo processHandleInfo)
    {
        _processHandleInfo = processHandleInfo;
        _process = process;
    }

    public string ProcessName => _processHandleInfo.ProcessName ?? "<unknown>";

    public int Id => _processHandleInfo.Id;

    public TextWriter StandardInput => _process.StandardInput;

    public TextReader StandardOutput => _process.StandardOutput;

    public int ExitCode => _process.ExitCode;

    public async Task<int> WaitForExitAsync()
    {
        if (!_disposed)
        {
            await _process.WaitForExitAsync();
        }

        return _exitCode;
    }

    public void WaitForExit() => _process.WaitForExit();

    public async Task<int> StopAsync()
    {
        if (_disposed)
        {
            return _exitCode;
        }

        KillSafe(_process);
        return await WaitForExitAsync();
    }

    public void Kill()
    {
        if (_disposed)
        {
            return;
        }

        KillSafe(_process);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_process)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        KillSafe(_process);
        _process.WaitForExit();
        _exitCode = _process.ExitCode;
        _process.Dispose();
    }

    public async Task WriteInputAsync(string input)
    {
        await _process.StandardInput.WriteLineAsync(input);
        await _process.StandardInput.FlushAsync();
    }

    private static void KillSafe(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}

public static class StringExtensions
{
    // Double checking that is is not null on purpose.
    public static string OrDefault(this string? value, string defaultValue) => string.IsNullOrEmpty(defaultValue)
                ? throw new ArgumentNullException(nameof(defaultValue))
                : !string.IsNullOrWhiteSpace(value)
                    ? value!
                    : defaultValue;
}

public static class WellKnownEnvironmentVariables
{
    public static readonly string[] ToSkipEnvironmentVariables =
    [
        // Skip dotnet root, we redefine it below.
        "DOTNET_ROOT",

        // Skip all environment variables related to minidump functionality.
        // https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/xplat-minidump-generation.md
        "DOTNET_DbgEnableMiniDump",
        "DOTNET_DbgMiniDumpName",
        "DOTNET_CreateDumpDiagnostics",
        "DOTNET_CreateDumpVerboseDiagnostics",
        "DOTNET_CreateDumpLogToFile",
        "DOTNET_EnableCrashReport",
        "DOTNET_EnableCrashReportOnly",

        // Old syntax for the minidump functionality.
        "COMPlus_DbgEnableMiniDump",
        "COMPlus_DbgEnableElfDumpOnMacOS",
        "COMPlus_DbgMiniDumpName",
        "COMPlus_DbgMiniDumpType",

        // Hot reload mode
        "TESTINGPLATFORM_HOTRELOAD_ENABLED",

        // Telemetry
        // By default arcade set this environment variable
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "TESTINGPLATFORM_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO",
        "TESTINGPLATFORM_NOBANNER",

        // Diagnostics
        "TESTINGPLATFORM_DIAGNOSTIC",

        // Isolate from the skip banner in case of parent, children tests
        "TESTINGPLATFORM_CONSOLEOUTPUTDEVICE_SKIP_BANNER"
    ];
}
