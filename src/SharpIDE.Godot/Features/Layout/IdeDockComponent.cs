using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class IdeDockComponent : VBoxContainer
{
	public IdeSceneNode ComponentNode { get; set; } = null!;

	public override void _Ready()
	{
		GetNode<Label>("%ComponentName").Text = ComponentNode.Name;
		
		var sceneResource = ResourceLoader.Load<PackedScene>(ComponentNode.ResourceUid);
		var scene = sceneResource.Instantiate<Control>();
		GetNode<Control>("%ScenePresenter").AddChild(scene);

		scene.VisibilityChanged += () => Visible = scene.Visible;
	}
}
