using Godot;

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class AutoOpenLastSlnButton : CheckBox
{
	public override void _Ready()
	{
		ButtonPressed = Singletons.AppState.IdeSettings.AutoOpenLastSolution;
		Pressed += () => Singletons.AppState.IdeSettings.AutoOpenLastSolution = ButtonPressed;
	}
}
