using Godot;

namespace SharpIDE.Godot.Features.Common;

public static class OptionButtonExtensions
{
	/// Shows the button's popup above it, rather than below, when there isn't room below and there is above. Godot always opens it below the button, then pushes it
	/// up to stay inside the window - over the button, so it covers what's being chosen. Call once, e.g. in _Ready
	public static void OpenPopupAboveWhenNoRoomBelow(this OptionButton button)
	{
		var popup = button.GetPopup();
		popup.VisibilityChanged += () =>
		{
			// By now the popup has its final position and size, but hasn't been drawn - so moving it doesn't flicker
			if (popup.Visible is false) return;
			// An embedded popup is positioned in its embedder's (the root window's) space - not the screen's, which GetScreenPosition gives and the root's
			// ContentScaleFactor (the IDE's UI scale) scales. So the button and the room around it are measured in the root's space too
			var root = button.GetTree().Root;
			if (popup.IsEmbedded() is false) return;
			var buttonRect = button.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, button.Size);
			var bounds = root.GetVisibleRect();
			var popupHeight = popup.Size.Y;

			var roomBelow = bounds.End.Y - buttonRect.End.Y;
			var roomAbove = buttonRect.Position.Y - bounds.Position.Y;
			// Fits below (where Godot put it), or doesn't fit above either - leave it where Godot put it
			if (popupHeight <= roomBelow || popupHeight > roomAbove) return;
			popup.Position = popup.Position with { Y = Mathf.RoundToInt(buttonRect.Position.Y - popupHeight) };
		};
	}
}
