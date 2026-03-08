using System.Collections.Immutable;
using System.Diagnostics;
using Ardalis.GuardClauses;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Events;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private readonly Texture2D _csharpMethodIcon = ResourceLoader.Load<Texture2D>("uid://b17p18ijhvsep");
    private readonly Texture2D _csharpClassIcon = ResourceLoader.Load<Texture2D>("uid://b027uufaewitj");
    private readonly Texture2D _csharpInterfaceIcon = ResourceLoader.Load<Texture2D>("uid://bdwmkdweqvowt");
    private readonly Texture2D _localVariableIcon = ResourceLoader.Load<Texture2D>("uid://vwvkxlnvqqk3");
    private readonly Texture2D _fieldIcon = ResourceLoader.Load<Texture2D>("uid://c4y7d5m4upfju");
    private readonly Texture2D _parameterIcon = ResourceLoader.Load<Texture2D>("uid://b0bv71yfmd08f");
    private readonly Texture2D _propertyIcon = ResourceLoader.Load<Texture2D>("uid://y5pwrwwrjqmc");
    private readonly Texture2D _keywordIcon = ResourceLoader.Load<Texture2D>("uid://b0ujhoq2xg2v0");
    private readonly Texture2D _namespaceIcon = ResourceLoader.Load<Texture2D>("uid://bob5blfjll4h3");
    private readonly Texture2D _eventIcon = ResourceLoader.Load<Texture2D>("uid://c3upo3lxmgtls");
    private readonly Texture2D _enumIcon = ResourceLoader.Load<Texture2D>("uid://8mdxo65qepqv");
    private readonly Texture2D _delegateIcon = ResourceLoader.Load<Texture2D>("uid://c83pv25rdescy");

    private Texture2D? GetIconForCompletion(SharpIdeCompletionItem sharpIdeCompletionItem)
    {
	    var completionItem = sharpIdeCompletionItem.CompletionItem;
	    var symbolKindString = CollectionExtensions.GetValueOrDefault(completionItem.Properties, "SymbolKind");
	    var symbolKind = symbolKindString is null ? null : (SymbolKind?)int.Parse(symbolKindString);
	    var wellKnownTags = completionItem.Tags;
	    var typeKindString = completionItem.Tags.ElementAtOrDefault(0);
	    var accessibilityModifierString = completionItem.Tags.Skip(1).FirstOrDefault(); // accessibility is not always supplied, and I don't think there's actually any guarantee on the order of tags. See WellKnownTags and WellKnownTagArrays
	    TypeKind? typeKind = Enum.TryParse<TypeKind>(typeKindString, out var tk) ? tk : null;
	    Accessibility? accessibilityModifier = Enum.TryParse<Accessibility>(accessibilityModifierString, out var am) ? am : null;
		
	    var isKeyword = wellKnownTags.Contains(WellKnownTags.Keyword);
	    var isExtensionMethod = wellKnownTags.Contains(WellKnownTags.ExtensionMethod);
	    var isMethod = wellKnownTags.Contains(WellKnownTags.Method);
	    if (symbolKind is null && (isMethod || isExtensionMethod)) symbolKind = SymbolKind.Method;
	    var icon = GetIconForCompletion(symbolKind, typeKind, accessibilityModifier, isKeyword);
	    return icon;
    }

    private Texture2D? GetIconForCompletion(SymbolKind? symbolKind, TypeKind? typeKind, Accessibility? accessibility, bool isKeyword)
    {
        if (isKeyword) return _keywordIcon;
        var texture = (symbolKind, typeKind, accessibility) switch
        {
            (SymbolKind.Method, _, _) => _csharpMethodIcon,
            (_, TypeKind.Interface, _) => _csharpInterfaceIcon,
            (_, TypeKind.Enum, _) => _enumIcon,
            (_, TypeKind.Delegate, _) => _delegateIcon,
            (_, TypeKind.Class, _) => _csharpClassIcon,
            (_, TypeKind.Struct, _) => _csharpClassIcon,
            (SymbolKind.NamedType, _, _) => _csharpClassIcon,
            (SymbolKind.Local, _, _) => _localVariableIcon,
            (SymbolKind.Field, _, _) => _fieldIcon,
            (SymbolKind.Parameter, _, _) => _parameterIcon,
            (SymbolKind.Property, _, _) => _propertyIcon,
            (SymbolKind.Namespace, _, _) => _namespaceIcon,
            (SymbolKind.Event, _, _) => _eventIcon,
            _ => null
        };    
        return texture;
    }
    
	private EventWrapper<CompletionTrigger, string, (int,int), Task> CustomCodeCompletionRequested { get; } = new((_, _, _) => Task.CompletedTask);
	private CompletionList? _completionList;
	private Document? _completionResultDocument;
	private CompletionTrigger? _completionTrigger;
	private CompletionTrigger? _pendingCompletionTrigger;
	private CompletionFilterReason? _pendingCompletionFilterReason;
	
	private readonly List<string> _codeCompletionTriggers =
	[
		"a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
		"_", "<", ".", "#"
	];

    private void ResetCompletionPopupState()
    {
	    _codeCompletionOptions = ImmutableArray<SharpIdeCompletionItem>.Empty;
        _completionList = null;
        _completionResultDocument = null;
        _completionTrigger = null;
        _completionTriggerPosition = null;
        _codeCompletionCurrentSelected = 0;
        _codeCompletionMinLineWidth = 0;
        _codeCompletionForceItemCenter = -1;
        _completionDescriptionWindow.Hide();
        _selectedCompletionDescription = null;
        _completionDescriptionLabel.Clear();
    }

    private async Task CustomFilterCodeCompletionCandidates(CompletionFilterReason filterReason)
    {
        if (_completionList is null || _completionList.ItemsList.Count is 0) return;
        var cursorPosition = await this.InvokeAsync(() => GetCaretPosition());
        var linePosition = new LinePosition(cursorPosition.line, cursorPosition.col);
        var filteredCompletions = RoslynAnalysis.FilterCompletions(_currentFile, Text, linePosition, _completionList, _completionTrigger!.Value, filterReason);
        if (filteredCompletions.Length is 0)
        {
	        await this.InvokeAsync(() =>
	        {
		        ResetCompletionPopupState();
		        QueueRedraw();
	        });
	        return;
        }
        var newSelectedIndex = 0;
        if (_codeCompletionOptions.IsDefaultOrEmpty is false)
        {
	        var currentSelectedCompletion = _codeCompletionOptions[_codeCompletionCurrentSelected];
	        var selectedCompletionInFilteredCompletions = filteredCompletions.Cast<SharpIdeCompletionItem?>().SingleOrDefault(s => s!.Value.CompletionItem == currentSelectedCompletion.CompletionItem);
	        if (selectedCompletionInFilteredCompletions is not null)
	        {
		        newSelectedIndex = filteredCompletions.IndexOf(selectedCompletionInFilteredCompletions.Value);
		        if (newSelectedIndex is -1) throw new UnreachableException();
	        }
        }
        _codeCompletionOptions = filteredCompletions;
        SetSelectedCompletion(newSelectedIndex);
        await this.InvokeAsync(QueueRedraw);
    }
    
	private async Task OnCodeCompletionRequested(CompletionTrigger completionTrigger, string documentTextAtTimeOfCompletionRequest, (int, int) completionCaretPosition)
	{
		var (caretLine, caretColumn) = completionCaretPosition;
		
		GD.Print($"Code completion requested at line {caretLine}, column {caretColumn}");
		var linePos = new LinePosition(caretLine, caretColumn);
				
		var completionsResult = await _roslynAnalysis.GetCodeCompletionsForDocumentAtPosition(_currentFile, documentTextAtTimeOfCompletionRequest, linePos, completionTrigger);
			
		// We can't draw until we get this position
		_completionTriggerPosition = await this.InvokeAsync(() => GetPosAtLineColumn(completionsResult.LinePosition.Line, completionsResult.LinePosition.Character));
			
		_completionList = completionsResult.CompletionList;
		_completionResultDocument = completionsResult.Document;
		var filterReason = completionTrigger.Kind switch
		{
			CompletionTriggerKind.Insertion => CompletionFilterReason.Insertion,
			CompletionTriggerKind.Deletion => CompletionFilterReason.Deletion,
			CompletionTriggerKind.InvokeAndCommitIfUnique => CompletionFilterReason.Other,
			_ => throw new ArgumentOutOfRangeException(nameof(completionTrigger.Kind), completionTrigger.Kind, null),
		};
		await CustomFilterCodeCompletionCandidates(filterReason);
		GD.Print($"Found {completionsResult.CompletionList.ItemsList.Count} completions, displaying menu");
	}

	public void ApplySelectedCodeCompletion()
	{
		var selectedIndex = _codeCompletionCurrentSelected;
		var completionItem = _codeCompletionOptions[selectedIndex];
		var document = _completionResultDocument;
		_ = Task.GodotRun(async () =>
		{
			Guard.Against.Null(document);
			await _ideApplyCompletionService.ApplyCompletion(_currentFile, completionItem.CompletionItem, document);
		});
		ResetCompletionPopupState();
		QueueRedraw();
	}
}
