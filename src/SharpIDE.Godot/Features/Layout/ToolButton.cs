using Godot;

using SharpIDE.Godot.Features.Tools;

namespace SharpIDE.Godot.Features.Layout;

public partial class ToolButton : Button
{
	public IdeToolId ToolId { get; set; }
	
	[Export]
	public Color DragPreviewColor { get; set; }

	/// <inheritdoc />
	public override Variant _GetDragData(Vector2 atPosition)
	{
		SetDragPreview(CreateDragPreview());

		// We disable the button when dragging to ensure the Hide() does not toggle it.
		Disabled = true;
		Hide();
		
		return Variant.From(ToolId);
	}

	/// <inheritdoc />
	public override void _Notification(int what)
	{
		switch ((long) what)
		{
			case NotificationDragEnd:
				Disabled = false;
				Show();
				break;
		}
	}

	private Control CreateDragPreview()
	{
		var rect = new ColorRect();
		rect.Size = Size;
		rect.Color = DragPreviewColor;
		
		var icon = new TextureRect
		{
			Texture = Icon,
			ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
		};
		
		icon.SetAnchorsPreset(LayoutPreset.FullRect);
		
		rect.AddChild(icon);
		return rect;
	}
}
