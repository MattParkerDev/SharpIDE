using Godot;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsButton : Button
{
	private SettingsWindow _settingsWindow = null!;

	public override void _Ready()
	{
		_settingsWindow = GetNode<SettingsWindow>("%SettingsWindow");
		_settingsWindow.Hide();
		Pressed += OnPressed;
	}

	private void OnPressed()
	{
		_settingsWindow.PopupCenteredRatio(0.5f);
	}
}
