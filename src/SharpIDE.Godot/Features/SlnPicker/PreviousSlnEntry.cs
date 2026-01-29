using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class PreviousSlnEntry : MarginContainer
{
    private Label _slnPathLabel = null!;
    private Label _slnNameLabel = null!;
    private Panel _slnColourPanel = null!;
    private Button _slnEntryButton = null!;
    
    public RecentSln RecentSln { get; set; } = null!;
    public Action<string>? Clicked;

    public override void _Ready()
    {
        if (RecentSln is null) return;
        _slnNameLabel = GetNode<Label>("%SlnNameLabel");
        _slnPathLabel = GetNode<Label>("%SlnPathLabel");
        _slnColourPanel = GetNode<Panel>("%Panel");
        _slnEntryButton = GetNode<Button>("Button");
        _slnNameLabel.Text = RecentSln.Name;
        _slnPathLabel.Text = RecentSln.FilePath;
        _slnColourPanel.Modulate = RandomRecentSlnColours.GetColourForFilePath(RecentSln.FilePath);
        _slnEntryButton.Pressed += () => Clicked?.Invoke(RecentSln.FilePath);
    }
}