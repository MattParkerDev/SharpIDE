using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.CodeEditor;

/// <summary>
/// A lightweight placeholder for an editor tab. The (expensive) <see cref="SharpIdeCodeEditContainer"/> is only
/// instantiated when the tab is first shown, so restoring many tabs on solution open is cheap.
/// </summary>
public partial class CodeEditorTab : MarginContainer
{
	public SharpIdeFile File { get; private set; } = null!;
	/// The caret position to apply once the editor is loaded
	public SharpIdeFileLinePosition? PendingLinePosition { get; set; }
	public SharpIdeCodeEditContainer? EditorContainer { get; private set; }
	public SharpIdeCodeEdit? CodeEdit => EditorContainer?.CodeEdit;
	public bool IsLoaded => EditorContainer is not null;

	private SharpIdeFileLinePosition? _loadLinePosition;
	private Task _loadTask = Task.CompletedTask;

	public static CodeEditorTab Create(SharpIdeFile file, SharpIdeFileLinePosition? pendingLinePosition) => new()
	{
		File = file,
		PendingLinePosition = pendingLinePosition,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
		SizeFlagsVertical = SizeFlags.ExpandFill,
	};

	/// Instantiates the editor if it hasn't been already. Idempotent - returns the in-progress/completed load task.
	[RequiresGodotUiThread]
	public Task EnsureLoaded(PackedScene codeEditScene, SharpIdeSolutionModel solution)
	{
		if (EditorContainer is not null) return _loadTask;
		var container = codeEditScene.Instantiate<SharpIdeCodeEditContainer>();
		container.CodeEdit.Solution = solution;
		AddChild(container);
		EditorContainer = container;
		_loadLinePosition = PendingLinePosition;
		PendingLinePosition = null;
		_loadTask = container.CodeEdit.SetSharpIdeFile(File, _loadLinePosition);
		return _loadTask;
	}

	/// The caret position, or the position the editor will be placed at once loaded
	[RequiresGodotUiThread]
	public SharpIdeFileLinePosition GetCaretPosition()
	{
		if (CodeEdit is { } codeEdit && IsInstanceValid(codeEdit))
		{
			if (_loadTask.IsCompletedSuccessfully) return new SharpIdeFileLinePosition(codeEdit.GetCaretLine(), codeEdit.GetCaretColumn());
			return _loadLinePosition ?? new SharpIdeFileLinePosition(0, 0);
		}
		return PendingLinePosition ?? new SharpIdeFileLinePosition(0, 0);
	}
}
