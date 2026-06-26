namespace SharpIDE.Godot.Features.IdeSettings;

public class IdeSolutionState
{
    public List<OpenTab> OpenTabs { get; set; } = [];
    public string LastStartupProject { get; set; } = "";
}

public class OpenTab
{
    public required string FilePath { get; set; }
    public required int CaretLine { get; set; }
    public required int CaretColumn { get; set; }
    public required bool IsSelected { get; set; }
}