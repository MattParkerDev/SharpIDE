using Godot;

namespace SharpIDE.Godot.Features.About;

public partial class AboutDialog : Window
{
	private Label _versionLabel = null!;

	public override void _Ready()
	{
		_versionLabel = GetNode<Label>("%VersionLabel");
		_versionLabel.Text = $"v{Singletons.SharpIdeVersion.ToNormalizedString()}";
	}
}
