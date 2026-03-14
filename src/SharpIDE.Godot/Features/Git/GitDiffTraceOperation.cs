using System.Diagnostics;
using SharpIDE.Application;

namespace SharpIDE.Godot.Features.Git;

[Flags]
internal enum GitDiffTraceRedrawTarget
{
    None = 0,
    BaseEditor = 1 << 0,
    CurrentEditor = 1 << 1,
    ActionGutter = 1 << 2
}

internal sealed class GitDiffTraceOperation
{
    private readonly Activity? _rootActivity;
    private readonly object _sync = new();
    private CancellationTokenSource? _redrawTimeoutCts;
    private int _pendingRedrawTargets;
    private bool _completed;

    public GitDiffTraceOperation(string activityName, string sourcePath)
    {
        _rootActivity = SharpIdeOtel.Source.StartActivity(activityName, ActivityKind.Internal);
        _rootActivity?.SetTag("git.diff.source_path", sourcePath);
    }

    public bool HasPendingRedraws => Volatile.Read(ref _pendingRedrawTargets) != 0;

    public void SetTag(string key, object? value)
    {
        _rootActivity?.SetTag(key, value);
    }

    public void AddEvent(string eventName)
    {
        _rootActivity?.AddEvent(new ActivityEvent(eventName));
    }

    public Activity? StartChild(string activityName)
    {
        if (_completed || _rootActivity is null)
        {
            return null;
        }

        return SharpIdeOtel.Source.StartActivity(activityName, ActivityKind.Internal, _rootActivity.Context);
    }

    public void ArmForRedraw(GitDiffTraceRedrawTarget redrawTargets)
    {
        if (_completed)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingRedrawTargets, (int)redrawTargets);
        _rootActivity?.SetTag("git.diff.pending_redraw_targets", redrawTargets.ToString());

        lock (_sync)
        {
            _redrawTimeoutCts?.Cancel();
            _redrawTimeoutCts?.Dispose();
            _redrawTimeoutCts = new CancellationTokenSource();
            _ = CompleteAfterRedrawTimeoutAsync(_redrawTimeoutCts.Token);
        }
    }

    public void MarkRedrawCompleted(GitDiffTraceRedrawTarget redrawTarget)
    {
        if (_completed)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _pendingRedrawTargets);
            if ((current & (int)redrawTarget) == 0)
            {
                return;
            }

            var next = current & ~(int)redrawTarget;
            if (Interlocked.CompareExchange(ref _pendingRedrawTargets, next, current) != current)
            {
                continue;
            }

            _rootActivity?.AddEvent(new ActivityEvent($"redraw.{redrawTarget}.completed"));
            if (next == 0)
            {
                Complete("redraw_completed");
            }

            return;
        }
    }

    public void CompleteIfNoPendingRedraws(string reason)
    {
        if (!HasPendingRedraws)
        {
            Complete(reason);
        }
    }

    public void Complete(string reason)
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _rootActivity?.SetTag("git.diff.complete_reason", reason);
            _rootActivity?.Stop();
            _redrawTimeoutCts?.Cancel();
            _redrawTimeoutCts?.Dispose();
            _redrawTimeoutCts = null;
        }
    }

    private async Task CompleteAfterRedrawTimeoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            Complete("redraw_timeout");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
