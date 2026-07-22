using Godot;

namespace SharpIDE.Godot.Features.Run.RunProjectsComponent;

public partial class RunProjectsComponent : MarginContainer
{
	private Button _runMenuButton = null!;
	private Popup _runMenuPopup = null!;

	private readonly PackedScene _runMenuItemScene = ResourceLoader.Load<PackedScene>("res://Features/Run/RunMenuItem.tscn");

    [Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;
	public override void _Ready()
	{
		_runMenuPopup = GetNode<Popup>("%RunMenuPopup");
		_runMenuButton = GetNode<Button>("%RunMenuButton");
		_runMenuButton.Pressed += OnRunMenuButtonPressed;
		_ = Task.GodotRun(AsyncReady);
	}

	public async Task AsyncReady()
	{
		await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
		var solutionModel = _sharpIdeSolutionAccessor.SolutionModel;
		var tasks = solutionModel.AllProjects.Select(p => p.MsBuildEvaluationProjectTask).ToList();
		await Task.WhenAll(tasks).ConfigureAwait(false);
		var runnableProjects = solutionModel.AllProjects.Where(p => p.IsLoaded && p.IsRunnable).ToList();
		await this.InvokeAsync(() =>
		{
			var runMenuPopupVbox = _runMenuPopup.GetNode<VBoxContainer>("MarginContainer/VBoxContainer");
			foreach (var project in runnableProjects)
			{
				var runMenuItem = _runMenuItemScene.Instantiate<RunMenuItem>();
				runMenuItem.Project = project;
				runMenuPopupVbox.AddChild(runMenuItem);
			}
			_runMenuButton.Disabled = false;
		});
	}

	private void OnRunMenuButtonPressed()
	{
		var popupMenuPosition = _runMenuButton.GlobalPosition;
		const int buttonHeight = 37;
		_runMenuPopup.Position = new Vector2I((int)popupMenuPosition.X, (int)popupMenuPosition.Y + buttonHeight);
		_runMenuPopup.Popup();
	}
}
