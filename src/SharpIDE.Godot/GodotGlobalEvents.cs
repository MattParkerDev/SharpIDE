using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.Layout;
using SharpIDE.Godot.Features.Tools;

namespace SharpIDE.Godot;

public class GodotGlobalEvents
{
    public static GodotGlobalEvents Instance { get; set; } = null!;
    public EventWrapper<IdeToolId, Task> IdeToolExternallyActivated { get; } = new(_ => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileSelected { get; } = new((_, _) => Task.CompletedTask);
    public EventWrapper<SharpIdeFile, SharpIdeFileLinePosition?, Task> FileExternallySelected { get; } = new((_, _) => Task.CompletedTask);
}