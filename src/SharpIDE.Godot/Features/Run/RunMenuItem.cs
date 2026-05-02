using Godot;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.Run;

public partial class RunMenuItem : Control
{
    [Signal]
    public delegate void PressedEventHandler();

    [Signal]
    public delegate void RunRequestedEventHandler(RunMenuItem sender);
    
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
        _label.Text = Project.Name.Value;
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
        MouseEntered += HandleMouseEntered;
        MouseExited += HandleMouseExited;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton iemb) return;
        if (iemb is {Pressed: true, ButtonIndex: MouseButton.Left})
            EmitSignalPressed();
    }

    private void HandleMouseEntered()
    {
        var sb = (StyleBoxFlat)_label.GetThemeStylebox("normal");
        var color = sb.BgColor;
        color.A = 255;
        sb.BgColor = color;
    }

    private void HandleMouseExited()
    {
        var sb = (StyleBoxFlat)_label.GetThemeStylebox("normal");
        var color = sb.BgColor;
        color.A = 0;
        sb.BgColor = color;
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
        EmitSignalRunRequested(this);
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
        EmitSignalRunRequested(this);
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