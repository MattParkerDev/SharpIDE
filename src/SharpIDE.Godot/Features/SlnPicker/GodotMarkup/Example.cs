using Godot;
using static SharpIDE.Godot.Features.SlnPicker.GodotMarkup.VNodeExtensions;

namespace SharpIDE.Godot.Features.SlnPicker.GodotMarkup;

public partial class Example : Node
{
	private List<string> _myStrings = ["one", "two", "three"];

	protected VNode Render() =>
		_<VBoxContainer>()
		[
			_<Label>(s => { s.Text = "Hello, World!"; s.TextDirection = Control.TextDirection.Auto; })
			[[
				_<Label>(),
				.. _myStrings.Select(s => _<Label>(l => l.Text = s))
			]],
			_<Label>()
		];

	public override void _Ready()
	{
		var root = Render().Build();
		AddChild(root);
	}
}
