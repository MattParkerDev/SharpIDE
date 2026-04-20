using Godot;

namespace SharpIDE.Godot.Features.SlnPicker;

/// ExtendToTitle is currently only implemented on MacOS, and is also currently disabled in the project
public partial class SlnPickerTitleBar : Control
{
	private Control _leftSpacer = null!;
	private Control _rightSpacer = null!;
	private Window _window = null!;
	
	private static bool ExtendToTitleEnabled => DisplayServer.HasFeature(DisplayServer.Feature.ExtendToTitle) && DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.ExtendToTitle);

	public override void _Ready()
	{
		if (ExtendToTitleEnabled is false)
		{
			return;
		}
		Visible = true;
		DisplayServer.WindowSetWindowButtonsOffset(new Vector2I(16, 16));
		_leftSpacer = GetNode<Control>("%LeftSpacer");
		_rightSpacer = GetNode<Control>("%RightSpacer");
		_window = GetWindow();
		Resized += UpdateDragArea;
		_window.TitlebarChanged += UpdateDragArea;
		UpdateDragArea();
	}

	private void UpdateDragArea()
	{
		// Register this control's rect as the non-client (draggable) area
		_window.SetNonclientArea(new Rect2I((Vector2I)GlobalPosition, (Vector2I)Size));

		// Use safe margins to avoid placing UI under traffic-light buttons
		// margins.X = left side width used by buttons (LTR)
		// margins.Y = right side width used by buttons (RTL)
		// margins.Z = height of the button area
		var margins = DisplayServer.WindowGetSafeTitleMargins();
		_leftSpacer.CustomMinimumSize = new Vector2(margins.X, 0);
		_rightSpacer.CustomMinimumSize = new Vector2(margins.X, 0);
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
		{
			DisplayServer.WindowStartDrag();
		}
	}
	
	public override void _ExitTree()
	{
		if (ExtendToTitleEnabled is false) return;
		_window.SetNonclientArea(new Rect2I());
		_window.TitlebarChanged -= UpdateDragArea;
		Resized -= UpdateDragArea;
		_window = null!;
		
		RequestReady();
	}
}
