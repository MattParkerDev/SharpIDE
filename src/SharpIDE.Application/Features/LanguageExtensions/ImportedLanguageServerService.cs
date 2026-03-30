using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using Nerdbank.Streams;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using StreamJsonRpc;

namespace SharpIDE.Application.Features.LanguageExtensions;

public sealed class ImportedLanguageServerService(LanguageExtensionRegistry registry, ILogger<ImportedLanguageServerService> logger)
{
    private readonly Dictionary<string, ImportedLanguageServerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public bool HasServerFor(SharpIdeFile file)
    {
        var extension = Path.GetExtension(file.Path);
        return registry.GetLanguageServerForExtension(extension) != null;
    }

    public async Task OpenDocumentAsync(SharpIdeFile file, string text, CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(file, cancellationToken);
        if (session == null)
            return;

        await session.OpenDocumentAsync(file, text, cancellationToken);
    }

    public async Task<ImmutableArray<SharpIdeClassifiedSpan>> OpenDocumentAndGetSemanticTokensAsync(
        SharpIdeFile file,
        string text,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(file, cancellationToken);
        if (session == null)
            return [];

        await session.OpenDocumentAsync(file, text, cancellationToken);
        return await session.GetSemanticTokensAsync(file, text, cancellationToken);
    }

    public async Task NotifyDocumentChangedAsync(SharpIdeFile file, string text, CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(file, cancellationToken);
        if (session == null)
            return;

        await session.ChangeDocumentAsync(file, text, cancellationToken);
    }

    public async Task<ImmutableArray<SharpIdeClassifiedSpan>> NotifyDocumentChangedAndGetSemanticTokensAsync(
        SharpIdeFile file,
        string text,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(file, cancellationToken);
        if (session == null)
            return [];

        await session.ChangeDocumentAsync(file, text, cancellationToken);
        return await session.GetSemanticTokensAsync(file, text, cancellationToken);
    }

    public async Task CloseDocumentAsync(SharpIdeFile file, CancellationToken cancellationToken = default)
    {
        var session = TryGetExistingSession(file);
        if (session == null)
            return;

        await session.CloseDocumentAsync(file, cancellationToken);
    }

    private async Task<ImportedLanguageServerSession?> GetOrCreateSessionAsync(SharpIdeFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.Path);
        var server = registry.GetLanguageServerForExtension(extension);
        if (server == null)
            return null;

        var workspaceRoot = ((IChildSharpIdeNode)file).GetNearestProjectNode()?.DirectoryPath
            ?? Path.GetDirectoryName(file.Path)
            ?? Environment.CurrentDirectory;

        var sessionKey = $"{server.LanguageId}|{workspaceRoot}|{server.Command}";

        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionKey, out var existing))
                return existing;
        }

        var created = await ImportedLanguageServerSession.CreateAsync(server, workspaceRoot, logger, cancellationToken);

        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionKey, out var raced))
            {
                _ = created.DisposeAsync().AsTask();
                return raced;
            }

            _sessions[sessionKey] = created;
            return created;
        }
    }

    private ImportedLanguageServerSession? TryGetExistingSession(SharpIdeFile file)
    {
        var extension = Path.GetExtension(file.Path);
        var server = registry.GetLanguageServerForExtension(extension);
        if (server == null)
            return null;

        var workspaceRoot = ((IChildSharpIdeNode)file).GetNearestProjectNode()?.DirectoryPath
            ?? Path.GetDirectoryName(file.Path)
            ?? Environment.CurrentDirectory;
        var sessionKey = $"{server.LanguageId}|{workspaceRoot}|{server.Command}";

        lock (_gate)
        {
            _sessions.TryGetValue(sessionKey, out var existing);
            return existing;
        }
    }
}

internal sealed class ImportedLanguageServerSession : IAsyncDisposable
{
    private readonly LanguageServerContribution _server;
    private readonly string _workspaceRoot;
    private readonly ILogger _logger;
    private readonly JsonRpc _rpc;
    private readonly Process _process;
    private readonly SemaphoreSlim _messageLock = new(1, 1);
    private readonly Dictionary<string, int> _documentVersions = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<string> _semanticTokenLegend = [];
    private ImmutableArray<string> _semanticTokenModifierLegend = [];
    private bool _initialized;
    private bool _loggedSemanticTokens;

    private ImportedLanguageServerSession(
        LanguageServerContribution server,
        string workspaceRoot,
        ILogger logger,
        JsonRpc rpc,
        Process process)
    {
        _server = server;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
        _rpc = rpc;
        _process = process;
    }

    public static async Task<ImportedLanguageServerSession> CreateAsync(
        LanguageServerContribution server,
        string workspaceRoot,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var processStartInfo = BuildProcessStartInfo(server, workspaceRoot);
        var process = new Process { StartInfo = processStartInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start language server '{server.Command}'.");

        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                logger.LogInformation("LSP stderr ({LanguageId}): {Line}", server.LanguageId, line);
            }
        }, cancellationToken);

        var formatter = new JsonMessageFormatter();
        var handler = new HeaderDelimitedMessageHandler(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(new ImportedLanguageServerClientTarget(logger, server.ConfigurationSections));
        rpc.Disconnected += (_, args) =>
        {
            logger.LogWarning(args.Exception, "Imported language server disconnected for {LanguageId}", server.LanguageId);
        };
        rpc.StartListening();

        var session = new ImportedLanguageServerSession(server, workspaceRoot, logger, rpc, process);
        await session.InitializeAsync(cancellationToken);
        return session;
    }

    public async Task OpenDocumentAsync(SharpIdeFile file, string text, CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var uri = ToDocumentUri(file.Path);
            if (_documentVersions.ContainsKey(uri))
                return;

            _documentVersions[uri] = 1;
            await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri,
                    languageId = _server.LanguageId,
                    version = 1,
                    text
                }
            });
        }
        finally
        {
            _messageLock.Release();
        }
    }

    public async Task ChangeDocumentAsync(SharpIdeFile file, string text, CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var uri = ToDocumentUri(file.Path);
            if (!_documentVersions.TryGetValue(uri, out var version))
            {
                _documentVersions[uri] = 1;
                await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri,
                        languageId = _server.LanguageId,
                        version = 1,
                        text
                    }
                });
                return;
            }

            version++;
            _documentVersions[uri] = version;
            await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
            {
                textDocument = new
                {
                    uri,
                    version
                },
                contentChanges = new object[]
                {
                    new { text }
                }
            });
        }
        finally
        {
            _messageLock.Release();
        }
    }

    public async Task CloseDocumentAsync(SharpIdeFile file, CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
                return;

            var uri = ToDocumentUri(file.Path);
            if (!_documentVersions.Remove(uri))
                return;

            await _rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new
            {
                textDocument = new
                {
                    uri
                }
            });
        }
        finally
        {
            _messageLock.Release();
        }
    }

    public async Task<ImmutableArray<SharpIdeClassifiedSpan>> GetSemanticTokensAsync(
        SharpIdeFile file,
        string text,
        CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            if (_semanticTokenLegend.IsDefaultOrEmpty)
                return [];

            var response = await _rpc.InvokeWithParameterObjectAsync<SemanticTokensResponse>("textDocument/semanticTokens/full", new
            {
                textDocument = new
                {
                    uri = ToDocumentUri(file.Path)
                }
            }, cancellationToken);

            if (response?.Data == null || response.Data.Length == 0)
                return [];

            LogSemanticTokenSample(response.Data);
            return DecodeSemanticTokens(text, response.Data, _semanticTokenLegend, _semanticTokenModifierLegend);
        }
        catch (RemoteInvocationException ex)
        {
            _logger.LogInformation(ex, "Semantic tokens request failed for {LanguageId}", _server.LanguageId);
            return [];
        }
        finally
        {
            _messageLock.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
        }
        finally
        {
            _messageLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        var workspaceUri = new Uri(Path.GetFullPath(_workspaceRoot)).AbsoluteUri;
        var initializationOptions = ParseInitializationOptions(_server.InitializationOptionsJson);

        var initializeResult = await _rpc.InvokeWithParameterObjectAsync<InitializeResponse>("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new
            {
                name = "SharpIDE",
                version = "dev"
            },
            rootUri = workspaceUri,
            capabilities = new
            {
                workspace = new
                {
                    configuration = true,
                workspaceFolders = true
                },
                textDocument = new
                {
                    publishDiagnostics = new { relatedInformation = true },
                    semanticTokens = new
                    {
                        dynamicRegistration = false,
                        requests = new
                        {
                            full = true,
                            range = false
                        },
                        tokenTypes = new[]
                        {
                            "namespace", "type", "class", "enum", "interface", "struct", "typeParameter",
                            "parameter", "variable", "property", "enumMember", "event", "function", "method",
                            "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator"
                        },
                        tokenModifiers = new[]
                        {
                            "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async",
                            "modification", "documentation", "defaultLibrary"
                        },
                        formats = new[] { "relative" }
                    },
                    synchronization = new { didSave = true, dynamicRegistration = false }
                }
            },
            workspaceFolders = new object[]
            {
                new
                {
                    uri = workspaceUri,
                    name = Path.GetFileName(_workspaceRoot)
                }
            },
            initializationOptions
        }, cancellationToken);

        (_semanticTokenLegend, _semanticTokenModifierLegend) = ReadSemanticTokenLegend(initializeResult);
        _logger.LogInformation(
            "Imported LSP semantic token legend for {LanguageId}: tokenTypes=[{TokenTypes}] tokenModifiers=[{TokenModifiers}]",
            _server.LanguageId,
            string.Join(", ", _semanticTokenLegend),
            string.Join(", ", _semanticTokenModifierLegend));

        await _rpc.NotifyWithParameterObjectAsync("initialized", new { });
        _initialized = true;
        _logger.LogInformation("Initialized imported language server {LanguageId} for {WorkspaceRoot}", _server.LanguageId, _workspaceRoot);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_initialized)
            {
                await _rpc.InvokeWithParameterObjectAsync<object?>("shutdown", new { });
                await _rpc.NotifyWithParameterObjectAsync("exit", new { });
            }
        }
        catch
        {
            // best effort
        }

        _rpc.Dispose();
        _messageLock.Dispose();

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
        }

        _process.Dispose();
    }

    private static ProcessStartInfo BuildProcessStartInfo(LanguageServerContribution server, string workspaceRoot)
    {
        var workingDirectory = workspaceRoot;
        if (!string.IsNullOrWhiteSpace(server.WorkingDirectory))
        {
            workingDirectory = Path.IsPathRooted(server.WorkingDirectory)
                ? server.WorkingDirectory
                : Path.Combine(Path.GetDirectoryName(server.Command) ?? workspaceRoot, server.WorkingDirectory);
        }

        var fileName = server.Command;
        var args = server.Args.Select(static arg => arg.Replace("{pid}", Environment.ProcessId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();

        if (server.Command.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            args.Insert(0, server.Command);
            fileName = "node";
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(" ", args.Select(QuoteArgument)),
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static object? ParseInitializationOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ToDocumentUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private void LogSemanticTokenSample(int[] tokenData)
    {
        if (_loggedSemanticTokens)
            return;

        _loggedSemanticTokens = true;
        var sample = tokenData.Take(Math.Min(tokenData.Length, 25));
        _logger.LogInformation(
            "Imported LSP semantic token sample for {LanguageId}: [{Sample}]",
            _server.LanguageId,
            string.Join(", ", sample));
    }

    private static (ImmutableArray<string> tokenTypes, ImmutableArray<string> tokenModifiers) ReadSemanticTokenLegend(InitializeResponse? initializeResult)
    {
        var legend = initializeResult?.Capabilities?.SemanticTokensProvider?.Legend;
        if (legend?.TokenTypes == null)
        {
            return ([], []);
        }

        var parsedTokenTypes = legend.TokenTypes
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToImmutableArray();

        var parsedTokenModifiers = legend.TokenModifiers?
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToImmutableArray() ?? [];

        return (parsedTokenTypes, parsedTokenModifiers);
    }

    private static ImmutableArray<SharpIdeClassifiedSpan> DecodeSemanticTokens(
        string text,
        int[] tokenData,
        ImmutableArray<string> legend,
        ImmutableArray<string> modifierLegend)
    {
        if (tokenData.Length == 0)
            return [];

        var lineStarts = BuildLineStarts(text);
        var result = ImmutableArray.CreateBuilder<SharpIdeClassifiedSpan>();
        int line = 0;
        int character = 0;

        for (var i = 0; i + 4 < tokenData.Length; i += 5)
        {
            line += tokenData[i];
            character = tokenData[i] == 0 ? character + tokenData[i + 1] : tokenData[i + 1];

            var length = tokenData[i + 2];
            var tokenTypeIndex = tokenData[i + 3];
            var modifiers = tokenData[i + 4];

            if (line < 0 || line >= lineStarts.Count || length <= 0)
                continue;

            if (tokenTypeIndex < 0 || tokenTypeIndex >= legend.Length)
                continue;

            var start = lineStarts[line] + character;
            if (start < 0 || start + length > text.Length)
                continue;

            var classification = MapSemanticTokenTypeToClassification(legend[tokenTypeIndex], modifiers, modifierLegend);
            var textSpan = new TextSpan(start, length);
            var fileSpan = new LinePositionSpan(
                new LinePosition(line, character),
                new LinePosition(line, character + length));

            result.Add(new SharpIdeClassifiedSpan(fileSpan, new ClassifiedSpan(classification, textSpan)));
        }

        return result.ToImmutable();
    }

    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }

        return starts;
    }

    private static string MapSemanticTokenTypeToClassification(
        string tokenType,
        int modifiers,
        ImmutableArray<string> modifierLegend)
    {
        var isStatic = HasModifier("static");
        var isReadonly = HasModifier("readonly");
        var isLocal = HasModifier("local");

        return tokenType switch
        {
            "namespace" => ClassificationTypeNames.NamespaceName,
            "type" or "class" => ClassificationTypeNames.ClassName,
            "enum" => ClassificationTypeNames.EnumName,
            "interface" => ClassificationTypeNames.InterfaceName,
            "struct" => ClassificationTypeNames.StructName,
            "typeParameter" => ClassificationTypeNames.TypeParameterName,
            "parameter" => ClassificationTypeNames.ParameterName,
            "variable" when isReadonly => ClassificationTypeNames.ConstantName,
            "variable" when isLocal => ClassificationTypeNames.LocalName,
            "variable" => ClassificationTypeNames.LocalName,
            "property" => ClassificationTypeNames.PropertyName,
            "enumMember" => ClassificationTypeNames.EnumMemberName,
            "event" => ClassificationTypeNames.EventName,
            "function" or "method" when isStatic => ClassificationTypeNames.StaticSymbol,
            "function" or "method" => ClassificationTypeNames.MethodName,
            "keyword" => ClassificationTypeNames.Keyword,
            "comment" => ClassificationTypeNames.Comment,
            "string" => ClassificationTypeNames.StringLiteral,
            "number" => ClassificationTypeNames.NumericLiteral,
            "operator" => ClassificationTypeNames.Operator,
            _ => ClassificationTypeNames.Identifier
        };

        bool HasModifier(string modifierName)
        {
            var modifierIndex = modifierLegend.IndexOf(modifierName);
            return modifierIndex >= 0 && (modifiers & (1 << modifierIndex)) != 0;
        }
    }

    private static string QuoteArgument(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal) || arg.Contains('"', StringComparison.Ordinal)
            ? $"\"{arg.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : arg;
}

internal sealed class InitializeResponse
{
    public InitializeCapabilities? Capabilities { get; init; }
}

internal sealed class InitializeCapabilities
{
    public SemanticTokensProviderInfo? SemanticTokensProvider { get; init; }
}

internal sealed class SemanticTokensProviderInfo
{
    public SemanticTokensLegendInfo? Legend { get; init; }
}

internal sealed class SemanticTokensLegendInfo
{
    public string[]? TokenTypes { get; init; }
    public string[]? TokenModifiers { get; init; }
}

internal sealed class SemanticTokensResponse
{
    public int[]? Data { get; init; }
}

internal sealed class ImportedLanguageServerClientTarget(ILogger logger, string[] configurationSections)
{
    private static readonly JsonElement EmptyConfig = JsonDocument.Parse("{}").RootElement.Clone();

    [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
    public object[] WorkspaceConfiguration(JsonElement request)
    {
        if (!request.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        return items.EnumerateArray()
            .Select(item =>
            {
                var section = item.TryGetProperty("section", out var sectionEl) ? sectionEl.GetString() : null;
                return section != null && configurationSections.Contains(section, StringComparer.OrdinalIgnoreCase)
                    ? EmptyConfig
                    : EmptyConfig;
            })
            .Cast<object>()
            .ToArray();
    }

    [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
    public void WindowLogMessage(JsonElement request)
    {
        logger.LogInformation("LSP log: {Payload}", request.ToString());
    }

    [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
    public void WindowShowMessage(JsonElement request)
    {
        logger.LogInformation("LSP showMessage: {Payload}", request.ToString());
    }

    [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
    public void PublishDiagnostics(JsonElement request)
    {
        logger.LogInformation("LSP diagnostics: {Payload}", request.ToString());
    }

    [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
    public Task RegisterCapabilityAsync(JsonElement _)
    {
        return Task.CompletedTask;
    }

    [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
    public Task UnregisterCapabilityAsync(JsonElement _)
    {
        return Task.CompletedTask;
    }
}
