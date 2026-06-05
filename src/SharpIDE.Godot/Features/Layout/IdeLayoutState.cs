using SharpIDE.Godot.Features.Tools;

namespace SharpIDE.Godot.Features.Layout;

public sealed record IdeLayoutState
{
    public static readonly IdeLayoutState Default = new()
    {
        SidebarTools = new Dictionary<ToolAnchor, List<IdeToolState>>
        {
            [ToolAnchor.LeftTop] =
            [
                new IdeToolState(IdeToolId.SolutionExplorer, ToolAnchor.LeftTop, IsActive: true)
            ],
            [ToolAnchor.RightTop] = [],
            [ToolAnchor.BottomLeft] =
            [
                new IdeToolState(IdeToolId.Problems, ToolAnchor.BottomLeft, IsActive: false),
                new IdeToolState(IdeToolId.Run, ToolAnchor.BottomLeft, IsActive: false),
                new IdeToolState(IdeToolId.Debug, ToolAnchor.BottomLeft, IsActive: false),
                new IdeToolState(IdeToolId.Build, ToolAnchor.BottomLeft, IsActive: false),
                new IdeToolState(IdeToolId.Nuget, ToolAnchor.BottomLeft, IsActive: false),
                new IdeToolState(IdeToolId.TestExplorer, ToolAnchor.BottomLeft, IsActive: false),
                new IdeToolState(IdeToolId.IdeDiagnostics, ToolAnchor.BottomLeft, IsActive: false)
            ],
            [ToolAnchor.BottomRight] = []
        }
    };

    public Dictionary<ToolAnchor, List<IdeToolState>> SidebarTools { get; init; } = [];
}