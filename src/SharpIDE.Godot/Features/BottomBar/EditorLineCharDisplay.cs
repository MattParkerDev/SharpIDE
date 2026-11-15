using Godot;
using SharpIDE.Application.Features.Editor;

namespace SharpIDE.Godot.Features.BottomBar;

public partial class EditorLineCharDisplay : HBoxContainer
{
	private Label _label = null!;
	private Label _selectionInfoLabel = null!;
	[Inject] private readonly EditorCaretPositionService _editorCaretPositionService = null!;

	private (int, int) _currentPositionRendered = (1, 1);
	private (int characters, int lineBreaks)? _currentSelectionInfo;

	public override void _Ready()
	{
		_label = GetNode<Label>("Label");
		_selectionInfoLabel = GetNode<Label>("%SelectionInfoLabel");
		_label.Text = null;
		_selectionInfoLabel.Text = null;
	}

	// Not sure if we should check this every frame, or an event with debouncing?
	public override void _Process(double delta)
	{
		var caretPosition = _editorCaretPositionService.CaretPosition;
		if (caretPosition != _currentPositionRendered)
		{
			_currentPositionRendered = caretPosition;
			_label.Text = $"{_currentPositionRendered.Item1}:{_currentPositionRendered.Item2}";
		}
		if (_editorCaretPositionService.SelectionInfo != _currentSelectionInfo)
		{
			_currentSelectionInfo = _editorCaretPositionService.SelectionInfo;
			if (_currentSelectionInfo is not null)
			{
				var characters = _currentSelectionInfo.Value.characters;
				var lineBreaks = _currentSelectionInfo.Value.lineBreaks;
				_selectionInfoLabel.Text = lineBreaks > 0 ? $"({characters} chars, {lineBreaks} line breaks)" : $"({characters} chars)";
				_selectionInfoLabel.Visible = true;
			}
			else
			{
				_selectionInfoLabel.Visible = false;
			}
		}
	}
}
