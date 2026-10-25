using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

public partial class NewSolutionFolderDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;

    public ISolutionOrSolutionFolder ParentNode { get; set; } = null!;

    [Inject] private readonly SharpIdeSolutionService _sharpIdeSolutionService = null!;

    public override void _Ready()
    {
        _nameLineEdit = GetNode<LineEdit>("%SolutionFolderNameLineEdit");
        _nameLineEdit.GrabFocus();
        _nameLineEdit.SelectAll();
        Confirmed += OnConfirmed;
        FocusExited += () =>
        {
	        // work around bug: https://github.com/godotengine/godot/issues/81370
	        Callable.From(GrabFocus).CallDeferred();
        };
        _nameLineEdit.TextSubmitted += _ =>
		{
			OnConfirmed();
			QueueFree();
		};
    }

    private void OnConfirmed()
    {
        var solutionFolderName = _nameLineEdit.Text.Trim();
        if (string.IsNullOrEmpty(solutionFolderName))
        {
            GD.PrintErr("Solution folder name cannot be empty.");
            return;
        }
        if (solutionFolderName.Contains('/') || solutionFolderName.Contains('\\'))
        {
            GD.PrintErr("Solution folder name cannot contain slashes.");
            return;
        }

        _ = Task.GodotRun(async () =>
        {
           await _sharpIdeSolutionService.AddSolutionFolder(ParentNode, solutionFolderName);
        });
    }
}
