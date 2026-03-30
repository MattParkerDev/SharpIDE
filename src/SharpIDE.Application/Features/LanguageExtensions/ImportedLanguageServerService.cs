using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
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

    public async Task<ImmutableArray<SharpIdeCompletionItem>> GetCodeCompletionsAsync(
        SharpIdeFile file,
        string text,
        LinePosition linePosition,
        CompletionTrigger completionTrigger,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(file, cancellationToken);
        if (session == null)
            return [];

        await session.OpenDocumentAsync(file, text, cancellationToken);
        return await session.GetCompletionsAsync(file, text, linePosition, completionTrigger, cancellationToken);
    }

    public Task<string?> GetCompletionDescriptionAsync(
        SharpIdeCompletionItem completionItem,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(completionItem.DescriptionText);
    }

    public async Task<(string updatedText, SharpIdeFileLinePosition caretPosition)> GetCompletionApplyChangesAsync(
        SharpIdeFile file,
        string currentText,
        LinePosition linePosition,
        SharpIdeCompletionItem completionItem,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(file, cancellationToken);
        if (session == null)
            throw new InvalidOperationException($"No imported language server is registered for '{file.Path}'.");

        await session.OpenDocumentAsync(file, currentText, cancellationToken);
        return session.ApplyCompletion(currentText, linePosition, completionItem);
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

    public async Task<ImmutableArray<SharpIdeCompletionItem>> GetCompletionsAsync(
        SharpIdeFile file,
        string text,
        LinePosition linePosition,
        CompletionTrigger completionTrigger,
        CancellationToken cancellationToken)
    {
        await _messageLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var response = await _rpc.InvokeWithParameterObjectAsync<CompletionListResponse?>("textDocument/completion", new
            {
                textDocument = new
                {
                    uri = ToDocumentUri(file.Path)
                },
                position = new
                {
                    line = linePosition.Line,
                    character = linePosition.Character
                },
                context = BuildCompletionContext(completionTrigger)
            }, cancellationToken);

            if (response?.Items == null || response.Items.Length == 0)
                return [];

            return response.Items
                .Select(item => ToSharpIdeCompletionItem(item, text, linePosition))
                .Where(static item => item.DisplayText.Length > 0)
                .ToImmutableArray();
        }
        catch (RemoteInvocationException ex)
        {
            _logger.LogInformation(ex, "Completion request failed for {LanguageId}", _server.LanguageId);
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

    public (string updatedText, SharpIdeFileLinePosition caretPosition) ApplyCompletion(
        string currentText,
        LinePosition linePosition,
        SharpIdeCompletionItem completionItem)
    {
        if (completionItem.ImportedData == null)
            throw new InvalidOperationException("Completion item does not belong to an imported language server.");

        var imported = completionItem.ImportedData;
        var sourceText = SourceText.From(currentText);
        var changes = new List<TextChange>();

        foreach (var edit in imported.AdditionalTextEdits)
        {
            if (TryCreateTextChange(sourceText, edit, out var textChange))
                changes.Add(textChange);
        }

        TextChange? primaryChange = TryCreatePrimaryCompletionChange(sourceText, linePosition, imported, out var resolvedPrimaryChange)
            ? resolvedPrimaryChange
            : null;

        if (primaryChange != null)
            changes.Add(primaryChange.Value);

        if (changes.Count == 0)
            throw new InvalidOperationException($"No applicable completion edits were returned for '{completionItem.DisplayText}'.");

        var updatedText = sourceText.WithChanges(changes.OrderByDescending(static change => change.Span.Start));
        var caretPosition = primaryChange is { } appliedPrimaryChange
            ? appliedPrimaryChange.Span.Start + appliedPrimaryChange.NewText.Length
            : sourceText.Lines.GetPosition(linePosition);

        var finalLinePosition = updatedText.Lines.GetLinePosition(caretPosition);
        return (
            updatedText.ToString(),
            new SharpIdeFileLinePosition
            {
                Line = finalLinePosition.Line,
                Column = finalLinePosition.Character
            });
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

    private static object BuildCompletionContext(CompletionTrigger completionTrigger)
    {
        var triggerKind = completionTrigger.Kind switch
        {
            CompletionTriggerKind.Insertion when completionTrigger.Character != '\0' => 2,
            _ => 1
        };

        return completionTrigger.Character != '\0'
            ? new
            {
                triggerKind,
                triggerCharacter = completionTrigger.Character.ToString()
            }
            : new
            {
                triggerKind
            };
    }

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

    private static SharpIdeCompletionItem ToSharpIdeCompletionItem(
        ImportedLspCompletionItem item,
        string currentText,
        LinePosition linePosition)
    {
        var filterText = item.FilterText ?? item.Label;
        var inlineDescription = item.Detail ?? item.LabelDetails?.Description ?? string.Empty;
        var displaySuffix = item.LabelDetails?.Detail ?? string.Empty;
        var description = BuildCompletionDescription(item);
        var kind = Enum.IsDefined(typeof(ImportedCompletionItemKind), item.Kind)
            ? (ImportedCompletionItemKind?)item.Kind
            : null;
        var matchedSpans = BuildSimpleMatchedSpans(item.Label, filterText, currentText, linePosition);

        return SharpIdeCompletionItem.FromImported(
            item.Label,
            displaySuffix,
            inlineDescription,
            description,
            new ImportedCompletionData
            {
                Label = item.Label,
                FilterText = filterText,
                SortText = item.SortText,
                InsertText = item.InsertText,
                InsertTextFormat = item.InsertTextFormat == 2 ? ImportedInsertTextFormat.Snippet : ImportedInsertTextFormat.PlainText,
                Kind = kind,
                TextEdit = item.TextEdit == null ? null : ToImportedTextEdit(item.TextEdit),
                AdditionalTextEdits = item.AdditionalTextEdits?
                    .Select(ToImportedTextEdit)
                    .ToImmutableArray() ?? []
            },
            matchedSpans);
    }

    private static string? BuildCompletionDescription(ImportedLspCompletionItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Detail))
            parts.Add(item.Detail);

        var documentation = item.Documentation.ValueKind switch
        {
            JsonValueKind.String => item.Documentation.GetString(),
            JsonValueKind.Object when item.Documentation.TryGetProperty("value", out var value) => value.GetString(),
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(documentation))
            parts.Add(documentation);

        return parts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static ImmutableArray<TextSpan>? BuildSimpleMatchedSpans(
        string label,
        string filterText,
        string currentText,
        LinePosition linePosition)
    {
        var candidate = GetCurrentCompletionFilterText(currentText, linePosition);
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var matchStart = label.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
        if (matchStart < 0)
            return null;

        return [new TextSpan(matchStart, Math.Min(candidate.Length, label.Length - matchStart))];
    }

    private static string GetCurrentCompletionFilterText(string currentText, LinePosition linePosition)
    {
        var sourceText = SourceText.From(currentText);
        if (linePosition.Line < 0 || linePosition.Line >= sourceText.Lines.Count)
            return string.Empty;

        var line = sourceText.Lines[linePosition.Line];
        var relativePosition = Math.Clamp(linePosition.Character, 0, line.Span.Length);
        var lineText = sourceText.ToString(line.Span);
        var start = relativePosition;
        while (start > 0 && IsCompletionIdentifierChar(lineText[start - 1]))
        {
            start--;
        }

        return lineText[start..relativePosition];
    }

    private static bool IsCompletionIdentifierChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '$' or ':';

    private static ImportedCompletionTextEdit ToImportedTextEdit(ImportedLspTextEdit textEdit)
    {
        return new ImportedCompletionTextEdit
        {
            NewText = textEdit.NewText ?? string.Empty,
            Range = textEdit.Range == null ? null : ToImportedRange(textEdit.Range),
            Insert = textEdit.Insert == null ? null : ToImportedRange(textEdit.Insert),
            Replace = textEdit.Replace == null ? null : ToImportedRange(textEdit.Replace)
        };
    }

    private static ImportedCompletionRange ToImportedRange(LspRange range)
    {
        return new ImportedCompletionRange
        {
            Start = new ImportedCompletionPosition
            {
                Line = range.Start?.Line ?? 0,
                Character = range.Start?.Character ?? 0
            },
            End = new ImportedCompletionPosition
            {
                Line = range.End?.Line ?? 0,
                Character = range.End?.Character ?? 0
            }
        };
    }

    private static bool TryCreatePrimaryCompletionChange(
        SourceText sourceText,
        LinePosition linePosition,
        ImportedCompletionData imported,
        out TextChange textChange)
    {
        if (imported.TextEdit != null && TryCreateTextChange(sourceText, imported.TextEdit, out textChange))
            return true;

        var lineIndex = Math.Clamp(linePosition.Line, 0, Math.Max(sourceText.Lines.Count - 1, 0));
        var line = sourceText.Lines[lineIndex];
        var absolutePosition = line.Start + Math.Clamp(linePosition.Character, 0, line.Span.Length);
        var replaceStart = absolutePosition;

        while (replaceStart > line.Start && IsCompletionIdentifierChar(sourceText[replaceStart - 1]))
        {
            replaceStart--;
        }

        var replacementSpan = TextSpan.FromBounds(replaceStart, absolutePosition);
        var newText = GetCompletionInsertText(imported);
        textChange = new TextChange(replacementSpan, newText);
        return true;
    }

    private static bool TryCreateTextChange(SourceText sourceText, ImportedCompletionTextEdit textEdit, out TextChange textChange)
    {
        var range = textEdit.Replace ?? textEdit.Insert ?? textEdit.Range;
        if (range == null)
        {
            textChange = default;
            return false;
        }

        var span = ToTextSpan(sourceText, range);
        textChange = new TextChange(span, ExpandInsertText(textEdit.NewText, ImportedInsertTextFormat.PlainText));
        return true;
    }

    private static TextSpan ToTextSpan(SourceText sourceText, ImportedCompletionRange range)
    {
        var start = sourceText.Lines.GetPosition(new LinePosition(range.Start.Line, range.Start.Character));
        var end = sourceText.Lines.GetPosition(new LinePosition(range.End.Line, range.End.Character));
        return TextSpan.FromBounds(start, end);
    }

    private static string GetCompletionInsertText(ImportedCompletionData imported)
    {
        var rawInsertText = imported.InsertText ?? imported.Label;
        return ExpandInsertText(rawInsertText, imported.InsertTextFormat);
    }

    private static string ExpandInsertText(string text, ImportedInsertTextFormat format)
    {
        if (format != ImportedInsertTextFormat.Snippet || string.IsNullOrEmpty(text))
            return text;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '$')
            {
                builder.Append(ch);
                continue;
            }

            if (i + 1 >= text.Length)
                continue;

            var next = text[i + 1];
            if (next == '{')
            {
                var closingBrace = text.IndexOf('}', i + 2);
                if (closingBrace < 0)
                    continue;

                var placeholderBody = text[(i + 2)..closingBrace];
                var colonIndex = placeholderBody.IndexOf(':');
                if (colonIndex >= 0 && colonIndex + 1 < placeholderBody.Length)
                    builder.Append(placeholderBody[(colonIndex + 1)..]);

                i = closingBrace;
                continue;
            }

            if (char.IsDigit(next))
            {
                i++;
                while (i + 1 < text.Length && char.IsDigit(text[i + 1]))
                    i++;
                continue;
            }

            builder.Append(next);
            i++;
        }

        return builder.ToString();
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

internal sealed class CompletionListResponse
{
    public bool IsIncomplete { get; init; }
    public ImportedLspCompletionItem[]? Items { get; init; }
}

internal sealed class ImportedLspCompletionItem
{
    public string Label { get; init; } = string.Empty;
    public int Kind { get; init; }
    public string? Detail { get; init; }
    public string? SortText { get; init; }
    public string? FilterText { get; init; }
    public string? InsertText { get; init; }
    public int InsertTextFormat { get; init; }
    public ImportedLspTextEdit? TextEdit { get; init; }
    public ImportedLspTextEdit[]? AdditionalTextEdits { get; init; }
    public JsonElement Documentation { get; init; }
    public ImportedLspLabelDetails? LabelDetails { get; init; }
}

internal sealed class ImportedLspLabelDetails
{
    public string? Detail { get; init; }
    public string? Description { get; init; }
}

internal sealed class ImportedLspTextEdit
{
    public string? NewText { get; init; }
    public LspRange? Range { get; init; }
    public LspRange? Insert { get; init; }
    public LspRange? Replace { get; init; }
}

internal sealed class LspRange
{
    public LspPosition? Start { get; init; }
    public LspPosition? End { get; init; }
}

internal sealed class LspPosition
{
    public int Line { get; init; }
    public int Character { get; init; }
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
