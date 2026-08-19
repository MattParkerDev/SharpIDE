using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.IdeSettings;
using SharpIDE.Godot.Features.ToolPanes;

namespace SharpIDE.Godot;

public class GodotGlobalEvents
{
    public static GodotGlobalEvents Instance { get; set; } = null!;
    public EventWrapper<ToolPaneType, Task> ToolPaneExternallyActivated { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileSelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileExternallySelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<SharpIdeProjectModel, StackFrameModel?, Task> DebuggerStackFrameSelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<LightOrDarkTheme, Task> TextEditorThemeChanged { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<bool, Task> TextEditorCodeFoldingChanged { get; } = new(_ => Task.CompletedTask);
}
