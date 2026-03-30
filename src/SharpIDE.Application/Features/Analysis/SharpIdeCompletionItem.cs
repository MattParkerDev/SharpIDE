using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

public readonly record struct SharpIdeCompletionItem
{
    public CompletionItem? RoslynCompletionItem { get; init; }
    public ImmutableArray<TextSpan>? MatchedSpans { get; init; }
    public string DisplayText { get; init; }
    public string DisplayTextSuffix { get; init; }
    public string InlineDescription { get; init; }
    public string? DescriptionText { get; init; }
    public ImportedCompletionData? ImportedData { get; init; }

    public bool IsRoslyn => RoslynCompletionItem != null;
    public bool IsImportedLanguageServer => ImportedData != null;

    public CompletionItem CompletionItem => RoslynCompletionItem
        ?? throw new InvalidOperationException("This completion item was not created by Roslyn.");

    public SharpIdeCompletionItem(CompletionItem completionItem, ImmutableArray<TextSpan>? matchedSpans)
    {
        RoslynCompletionItem = completionItem;
        MatchedSpans = matchedSpans;
        DisplayText = completionItem.DisplayText;
        DisplayTextSuffix = completionItem.DisplayTextSuffix;
        InlineDescription = completionItem.InlineDescription;
        DescriptionText = null;
        ImportedData = null;
    }

    public static SharpIdeCompletionItem FromImported(
        string displayText,
        string displayTextSuffix,
        string inlineDescription,
        string? descriptionText,
        ImportedCompletionData importedData,
        ImmutableArray<TextSpan>? matchedSpans = null)
    {
        return new SharpIdeCompletionItem
        {
            RoslynCompletionItem = null,
            MatchedSpans = matchedSpans,
            DisplayText = displayText,
            DisplayTextSuffix = displayTextSuffix,
            InlineDescription = inlineDescription,
            DescriptionText = descriptionText,
            ImportedData = importedData
        };
    }
}

public sealed record ImportedCompletionData
{
    public required string Label { get; init; }
    public string? FilterText { get; init; }
    public string? SortText { get; init; }
    public string? InsertText { get; init; }
    public ImportedInsertTextFormat InsertTextFormat { get; init; }
    public ImportedCompletionItemKind? Kind { get; init; }
    public ImportedCompletionTextEdit? TextEdit { get; init; }
    public ImmutableArray<ImportedCompletionTextEdit> AdditionalTextEdits { get; init; } = [];
}

public enum ImportedInsertTextFormat
{
    PlainText = 1,
    Snippet = 2
}

public enum ImportedCompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25
}

public sealed record ImportedCompletionTextEdit
{
    public required string NewText { get; init; }
    public ImportedCompletionRange? Range { get; init; }
    public ImportedCompletionRange? Insert { get; init; }
    public ImportedCompletionRange? Replace { get; init; }
}

public sealed record ImportedCompletionRange
{
    public required ImportedCompletionPosition Start { get; init; }
    public required ImportedCompletionPosition End { get; init; }
}

public sealed record ImportedCompletionPosition
{
    public required int Line { get; init; }
    public required int Character { get; init; }
}
