using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.Layout;

namespace SharpIDE.Godot;

public class GodotGlobalEvents
{
    public static GodotGlobalEvents Instance { get; set; } = null!;
    public EventWrapper<IdeTool, Task> IdeToolExternallySelected { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileSelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileExternallySelected { get; } = new((_, _) => Task.CompletedTask);
}