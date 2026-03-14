using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.SymbolLookup;

public partial class SymbolUsageComponent : MarginContainer
{
    private Label _enclosingSymbolLabel = null!;
    private Label _fileNameLabel = null!;
    private Label _lineNumberLabel = null!;
    private Button _button = null!;
    
    public SymbolLookupPopup ParentSearchWindow { get; set; } = null!;
    public ReferenceLocation? Location { get; set; }
    public ISymbol? EnclosingSymbol { get; set; } = null!;
    public SharpIdeFile? File { get; set; } = null!;
    
    public override void _Ready()
    {
        _button = GetNode<Button>("Button");
        _enclosingSymbolLabel = GetNode<Label>("%EnclosingSymbolLabel");
        _fileNameLabel = GetNode<Label>("%FileNameLabel");
        _lineNumberLabel = GetNode<Label>("%LineNumberLabel");
        SetValue(Location);
        _button.Pressed += OnButtonPressed;
    }

    private void OnButtonPressed()
    {
        var mappedLineSpan = Location!.Value.Location.GetMappedLineSpan();
        var fileLinePosition = new SharpIdeFileLinePosition { Line = mappedLineSpan.StartLinePosition.Line, Column = mappedLineSpan.StartLinePosition.Character };
        if (File is not null)
        {
            GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(File, fileLinePosition);
        }
        ParentSearchWindow.Hide();
    }

    private void SetValue(ReferenceLocation? result)
    {
        if (result is null) return;
        var mappedLineSpan = result.Value.Location.GetMappedLineSpan();
        
        _fileNameLabel.Text = File?.Name.Value ?? Path.GetFileName(mappedLineSpan.Path);
        _lineNumberLabel.Text = (mappedLineSpan.StartLinePosition.Line + 1).ToString();
        _enclosingSymbolLabel.Text = EnclosingSymbol?.Name;
    }
}