namespace SharpIDE.Godot.Features.IdeSettings;

public class AppState
{
    public string? LastOpenSolutionFilePath { get; set; }
    public IdeSettings IdeSettings { get; set; } = new IdeSettings();
    public List<RecentSln> RecentSlns { get; set; } = [];
}

public class IdeSettings
{
    public bool AutoOpenLastSolution { get; set; }
    public string? DebuggerExecutablePath { get; set; }
    public bool DebuggerUseSharpDbg { get; set; } = true;
    public float UiScale { get; set; } = 1.0f;
}

public record RecentSln
{
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public IdeSolutionState IdeSolutionState { get; set; } = new IdeSolutionState();
}