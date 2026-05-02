using Godot;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot;

public partial class StartupProject : HBoxContainer
{
	[Signal]
	public delegate void ProjectChangedEventHandler();
	
	private ProjectOptionButton _projectList = null!;
	private Control _spacer = null!;
	private Control _animatedBuilding = null!;
	private Button _runButton = null!;
	private Button _debugButton = null!;
	private Button _stopButton = null!;
	private AnimationPlayer _buildingAnim = null!;
	
	public ProjectOptionButton ProjectList => _projectList;

	[Inject] private readonly RunService _runService = null!;
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_projectList = GetNode<ProjectOptionButton>("%ProjectList");
		_spacer = GetNode<Control>("%Spacer");
		_animatedBuilding = GetNode<Control>("%AnimatedTextureParentControl");
		_runButton = GetNode<Button>("%RunButton");
		_debugButton = GetNode<Button>("%DebugButton");
		_stopButton = GetNode<Button>("%StopButton");
		_buildingAnim = GetNode<AnimationPlayer>("AnimationPlayer");

		foreach (var option in _projectList.Options)
		{
			option.Model.ProjectStartedRunning.Subscribe(() => OnProjectStartedRunning(option.Model));
			option.Model.ProjectStoppedRunning.Subscribe(() => OnProjectStoppedRunning(option.Model));
			option.Model.ProjectRunFailed.Subscribe(() => OnProjectRunFailed(option.Model));
		}

		_projectList.ItemAdded += (indx) =>
		{
			var option = _projectList.Options[indx];
			option.Model.ProjectStartedRunning.Subscribe(() => OnProjectStartedRunning(option.Model));
			option.Model.ProjectStoppedRunning.Subscribe(() => OnProjectStoppedRunning(option.Model));
			option.Model.ProjectRunFailed.Subscribe(() => OnProjectRunFailed(option.Model));
		};

		_projectList.RunRequested += (item) =>
		{
			if (item.Project == _projectList.CurrentRunOption.Model)
				SetAttemptingRunState();
		};
		
		_runButton.Pressed += OnRunButtonPressed;
		_stopButton.Pressed += OnStopButtonPressed;
		_debugButton.Pressed += OnDebugButtonPressed;

		_projectList.ProjectChanged += EmitSignalProjectChanged;
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event.IsActionPressed(InputStringNames.StopStartupProject))
		{
			AcceptEvent();
			OnStopButtonPressed();
		}
		else if (@event.IsActionPressed(InputStringNames.DebugStartupProject))
		{
			AcceptEvent();
			OnDebugButtonPressed();
		}
		else if (@event.IsActionPressed(InputStringNames.RunStartupProject))
		{
			AcceptEvent();
			OnRunButtonPressed();
		}
	}

	private async Task OnProjectRunFailed(SharpIdeProjectModel project)
	{
		if (_projectList.CurrentRunOption.Model != project) return;
		await this.InvokeAsync(() =>
		{
			_stopButton.Visible = false;
			_spacer.Visible = false;
			_debugButton.Visible = true;
			_runButton.Visible = true;
			_animatedBuilding.Visible = false;
			_buildingAnim.Stop();
		});
	}

	private async Task OnProjectStoppedRunning(SharpIdeProjectModel project)
	{
		if (_projectList.CurrentRunOption.Model != project) return;
		await this.InvokeAsync(() =>
		{
			_stopButton.Visible = false;
			_spacer.Visible = false;
			_debugButton.Visible = true;
			_runButton.Visible = true;
		});
	}

	private async Task OnProjectStartedRunning(SharpIdeProjectModel project)
	{
		if (_projectList.CurrentRunOption.Model != project) return;
		await this.InvokeAsync(() =>
		{
			_runButton.Visible = false;
			_debugButton.Visible = false;
			_stopButton.Visible = true;
			_animatedBuilding.Visible = false;
			_buildingAnim.Stop();
		});
	}

	private async void OnRunButtonPressed()
	{
		SetAttemptingRunState();
		await _runService.RunProject(_projectList.CurrentRunOption.Model).ConfigureAwait(false);
	}

	private async void OnDebugButtonPressed()
	{
		var debuggerExecutableInfo = new DebuggerExecutableInfo
		{
			UseInMemorySharpDbg = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg,
			DebuggerExecutablePath = Singletons.AppState.IdeSettings.DebuggerExecutablePath,
		};
		SetAttemptingRunState();
		await _runService.RunProject(_projectList.CurrentRunOption.Model, true, debuggerExecutableInfo).ConfigureAwait(false);
	}

	private async void OnStopButtonPressed()
	{
		await _runService.CancelRunningProject(_projectList.CurrentRunOption.Model);
	}

	private StringName _buildingAnimationName = "BuildingAnimation";
	private void SetAttemptingRunState()
	{
		_runButton.Visible = false;
		_debugButton.Visible = false;
		_spacer.Visible = true;
		_animatedBuilding.Visible = true;
		_buildingAnim.Play(_buildingAnimationName);
	}
}
