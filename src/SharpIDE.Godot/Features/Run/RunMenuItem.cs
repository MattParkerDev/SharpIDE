using Godot;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.BottomPanel;

namespace SharpIDE.Godot.Features.Run;

public partial class RunMenuItem : HBoxContainer
{
    public SharpIdeProjectModel Project { get; set; } = null!;
    private Label _label = null!;
    private Button _runButton = null!;
    private Button _debugButton = null!;
    private Button _stopButton = null!;
    private Control _animatedTextureParentControl = null!;
    private AnimationPlayer _buildAnimationPlayer = null!;
    
    [Inject] private readonly RunService _runService = null!;
    public override void _Ready()
    {
        _label = GetNode<Label>("Label");
        _label.Text = Project.Name;
        _runButton = GetNode<Button>("RunButton");
        _runButton.Pressed += OnRunButtonPressed;
        _stopButton = GetNode<Button>("StopButton");
        _stopButton.Pressed += OnStopButtonPressed;
        _debugButton = GetNode<Button>("DebugButton");
        _debugButton.Pressed += OnDebugButtonPressed;
        _animatedTextureParentControl = GetNode<Control>("%AnimatedTextureParentControl");
        _buildAnimationPlayer = GetNode<AnimationPlayer>("AnimationPlayer");
        Project.ProjectStartedRunning.Subscribe(OnProjectStartedRunning);
        Project.ProjectStoppedRunning.Subscribe(OnProjectStoppedRunning);
        Project.ProjectRunFailed.Subscribe(OnProjectRunFailed);
    }

    private async Task OnProjectRunFailed()
    {
        await this.InvokeAsync(() =>
        {
            _stopButton.Visible = false;
            _debugButton.Visible = true;
            _runButton.Visible = true;
            _animatedTextureParentControl.Visible = false;
            _buildAnimationPlayer.Stop();
        });
    }

    private async Task OnProjectStoppedRunning()
    {
        await this.InvokeAsync(() =>
        {
            _stopButton.Visible = false;
            _debugButton.Visible = true;
            _runButton.Visible = true;
        });
    }

    private async Task OnProjectStartedRunning()
    {
        await this.InvokeAsync(() =>
        {
            _runButton.Visible = false;
            _debugButton.Visible = false;
            _stopButton.Visible = true;
            _animatedTextureParentControl.Visible = false;
            _buildAnimationPlayer.Stop();
        });
    }

    private async void OnStopButtonPressed()
    {
        await _runService.CancelRunningProject(Project);
    }

    private StringName _buildAnimationName = "BuildingAnimation";
    private async void OnRunButtonPressed()
    {
        SetAttemptingRunState();
        await _runService.RunProject(Project).ConfigureAwait(false);
    }
    
    private async void OnDebugButtonPressed()
    {
        var debuggerExecutableInfo = new DebuggerExecutableInfo
        {
            UseInMemorySharpDbg = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg,
            DebuggerExecutablePath = Singletons.AppState.IdeSettings.DebuggerExecutablePath
        };
        SetAttemptingRunState();
        await _runService.RunProject(Project, true, debuggerExecutableInfo).ConfigureAwait(false);
    }
    
    private void SetAttemptingRunState()
    {
        _runButton.Visible = false;
        _debugButton.Visible = false;
        _animatedTextureParentControl.Visible = true;
        _buildAnimationPlayer.Play(_buildAnimationName);
    }
}