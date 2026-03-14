using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Search;

namespace SharpIDE.Godot.Features.Search;

public partial class SearchResultComponent : MarginContainer
{
    private Label _matchingLineLabel = null!;
    private Label _fileNameLabel = null!;
    private Label _lineNumberLabel = null!;
    private Button _button = null!;
    
    public SearchWindow ParentSearchWindow { get; set; } = null!;
    public FindInFilesSearchResult Result { get; set; } = null!;
    
    public override void _Ready()
    {
        _button = GetNode<Button>("Button");
        _matchingLineLabel = GetNode<Label>("%MatchingLineLabel");
        _fileNameLabel = GetNode<Label>("%FileNameLabel");
        _lineNumberLabel = GetNode<Label>("%LineNumberLabel");
        SetValue(Result);
        _button.Pressed += OnButtonPressed;
    }

    private void OnButtonPressed()
    {
        var fileLinePosition = new SharpIdeFileLinePosition { Line = Result.Line - 1, Column = Result.StartColumn - 1 };
        GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(Result.File, fileLinePosition);
        ParentSearchWindow.Hide();
    }

    private void SetValue(FindInFilesSearchResult result)
    {
        if (result is null) return;
        _matchingLineLabel.Text = result.LineText;
        _fileNameLabel.Text = result.File.Name.Value;
        _lineNumberLabel.Text = result.Line.ToString();
    }
}