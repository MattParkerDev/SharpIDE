using Godot;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsButton : Button
{
    private Window _settingsWindow = null!;
    
    public override void _Ready()
    {
        _settingsWindow = GetNode<Window>("%SettingsWindow");
        _settingsWindow.CloseRequested += () => _settingsWindow.Hide();
        Pressed += OnPressed;
    }

    private void OnPressed()
    {
        _settingsWindow.PopupCenteredRatio(0.5f);
    }
}