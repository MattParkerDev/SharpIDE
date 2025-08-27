namespace SharpIDE.Godot;

public static class GodotGlobalEvents
{
    public static event Func<BottomPanelType, Task> LeftSideBarButtonClicked = _ => Task.CompletedTask;
    public static void InvokeLeftSideBarButtonClicked(BottomPanelType type) => LeftSideBarButtonClicked?.Invoke(type);
}

public enum BottomPanelType
{
    Run,
    Build,
    Problems
}