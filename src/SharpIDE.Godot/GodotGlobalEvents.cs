using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Git;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.BottomPanel;
using SharpIDE.Godot.Features.IdeSettings;
using SharpIDE.Godot.Features.LeftSideBar;

namespace SharpIDE.Godot;

public class GodotGlobalEvents
{
    public static GodotGlobalEvents Instance { get; set; } = null!;
    public EventWrapper<LeftDockType, Task> LeftDockSelected { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<LeftDockType, Task> LeftDockExternallySelected { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<BottomPanelType, Task> BottomPanelTabExternallySelected { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<BottomPanelType?, Task> BottomPanelTabSelected { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<bool, Task> BottomPanelVisibilityChangeRequested { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileSelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileExternallySelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<string, Task> GitFilePreviewRequested { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<GitCommitFileDiffRequest, Task> GitCommitDiffRequested { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<GitCommitWorkingTreeDiffRequest, Task> GitCommitWorkingTreeDiffRequested { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<GitStashFileDiffRequest, Task> GitStashDiffRequested { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<Task> GitStatusesChanged { get; } = new(() => Task.CompletedTask);
    public EventWrapper<LightOrDarkTheme, Task> TextEditorThemeChanged { get; } = new(_ => Task.CompletedTask);
}
