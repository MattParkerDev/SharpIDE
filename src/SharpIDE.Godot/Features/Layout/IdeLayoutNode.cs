namespace SharpIDE.Godot.Features.Layout;

public abstract record IdeLayoutNode;

public enum Orientation
{
    Horizontal = 1,
    Vertical = 2
}

public enum DockScope
{
    None = 0,
    Global = 1,
    Local = 2
}

public enum DockPosition
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    Bottom = 4
}

public record IdeSplitNode(
    Orientation Orientation,
    IdeLayoutNode FirstNode,
    IdeLayoutNode SecondNode,
    float Ratio = 0.5f) : IdeLayoutNode;

public record IdeSceneNode(string ResourceUid) : IdeLayoutNode;

public record IdeTabGroupNode(
    string ResourceUid,
    List<IdeDocumentTab> DocumentTabs,
    int ActiveTabIndex) : IdeSceneNode(ResourceUid);

public record IdeDocumentTab(
    string TabId,
    string Label);