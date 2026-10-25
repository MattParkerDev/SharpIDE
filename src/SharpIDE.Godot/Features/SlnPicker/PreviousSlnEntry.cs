using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class PreviousSlnEntry : MarginContainer
{
    private Label _slnPathLabel = null!;
    private Label _slnNameLabel = null!;
    private Panel _slnColourPanel = null!;
    private Button _slnEntryButton = null!;
    private Button _removeButton = null!;
    
    public RecentSln RecentSln { get; set; } = null!;
    public Action<string>? Clicked;
    public Action<RecentSln>? RemoveClicked;

    public override void _Ready()
    {
        if (RecentSln is null) return;
        _slnNameLabel = GetNode<Label>("%SlnNameLabel");
        _slnPathLabel = GetNode<Label>("%SlnPathLabel");
        _slnColourPanel = GetNode<Panel>("%Panel");
        _slnEntryButton = GetNode<Button>("Button");
        _removeButton = GetNode<Button>("%RemoveButton");
        _slnNameLabel.Text = RecentSln.Name;
        _slnPathLabel.Text = RecentSln.FilePath;
        _slnColourPanel.Modulate = RandomRecentSlnColours.GetColourForFilePath(RecentSln.FilePath);
        _slnEntryButton.Pressed += () => Clicked?.Invoke(RecentSln.FilePath);
        _removeButton.Pressed += () => RemoveClicked?.Invoke(RecentSln);
        // Modulate rather than Visible, so the labels don't resize
        _slnEntryButton.MouseEntered += () => _removeButton.Modulate = Colors.White;
        // The remove button stops mouse events, so hovering it fires MouseExited on the entry - only hide once the cursor has actually left the entry
        _slnEntryButton.MouseExited += HideRemoveButtonIfMouseLeftEntry;
        _removeButton.MouseExited += HideRemoveButtonIfMouseLeftEntry;
    }

    private void HideRemoveButtonIfMouseLeftEntry()
    {
        if (_slnEntryButton.GetGlobalRect().HasPoint(_slnEntryButton.GetGlobalMousePosition())) return;
        _removeButton.Modulate = Colors.Transparent;
    }
}