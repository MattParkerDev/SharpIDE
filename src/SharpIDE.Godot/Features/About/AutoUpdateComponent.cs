using Godot;

namespace SharpIDE.Godot.Features.About;

public partial class AutoUpdateComponent : VBoxContainer
{
	private Label _lastCheckedAtLabel = null!;
	public override void _Ready()
	{
		_lastCheckedAtLabel = GetNode<Label>("%LastCheckedAtLabel");
		UpdateLastCheckedAtLabel();
	}

	private void UpdateLastCheckedAtLabel()
	{
		var lastChecked = Singletons.AppState.LastCheckedForUpdates;
		_lastCheckedAtLabel.Text = lastChecked is null
			? "Last checked at: never"
			: $"Last checked at: {lastChecked.Value.ToLocalTime():g}";
	}
}