using SharpIDE.Godot.Features.Tools;

namespace SharpIDE.Godot.Features.Layout;

public sealed record IdeToolState(
    IdeToolId ToolId,
    ToolAnchor Anchor,
    bool IsActive)
{
    /// <summary>
    ///     The ID of the tool.
    /// </summary>
    public IdeToolId ToolId { get; init; } = ToolId;

    /// <summary>
    ///     The current anchor of the tool.
    /// </summary>
    public ToolAnchor Anchor { get; set; } = Anchor;

    /// <summary>
    ///     Indicates if the tool is active.
    /// </summary>
    public bool IsActive { get; set; } = IsActive;
}