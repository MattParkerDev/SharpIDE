using System.Diagnostics;
using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Godot.Features.ActivityListener;

namespace SharpIDE.Godot.Features.BottomBar;

public partial class RunningTasksDisplay : HBoxContainer
{
    [Inject] private readonly ActivityMonitor _activityMonitor = null!;
    
    private bool _isSolutionRestoring;
    private bool _isSolutionLoading;
    private bool _isSolutionDiagnosticsBeingRetrieved;

    private Label _solutionRestoringLabel = null!;
    private Label _solutionLoadingLabel = null!;
    private Label _solutionDiagnosticsLabel = null!;
    
    public override void _Ready()
    {
        _solutionRestoringLabel = GetNode<Label>("%SolutionRestoringLabel");
        _solutionLoadingLabel = GetNode<Label>("%SolutionLoadingLabel");
        _solutionDiagnosticsLabel = GetNode<Label>("%SolutionDiagnosticsLabel");
        Visible = false;
        _activityMonitor.ActivityChanged.Subscribe(OnActivityChanged);
    }

    public override void _ExitTree()
    {
        _activityMonitor.ActivityChanged.Unsubscribe(OnActivityChanged);
    }

    private async Task OnActivityChanged(Activity activity)
    {
        var isOccurring = !activity.IsStopped;
        if (activity.DisplayName == $"{nameof(RoslynAnalysis)}.{nameof(RoslynAnalysis.UpdateSolutionDiagnostics)}")
        {
            _isSolutionDiagnosticsBeingRetrieved = isOccurring;
        }
        else if (activity.DisplayName == "OpenSolution")
        {
            _isSolutionLoading = isOccurring;
        }
        else if (activity.DisplayName == "RestoreSolution")
        {
            _isSolutionRestoring = isOccurring;
        }
        else
        {
            return;
        }
        
        var visible = _isSolutionDiagnosticsBeingRetrieved || _isSolutionLoading || _isSolutionRestoring;
        await this.InvokeAsync(() =>
        {
            _solutionLoadingLabel.Visible = _isSolutionLoading;
            _solutionDiagnosticsLabel.Visible = _isSolutionDiagnosticsBeingRetrieved;
            _solutionRestoringLabel.Visible = _isSolutionRestoring;
            Visible = visible;
        });
    }
}