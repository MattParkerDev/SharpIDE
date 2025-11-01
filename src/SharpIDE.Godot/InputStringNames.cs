using Godot;

namespace SharpIDE.Godot;

public static class InputStringNames
{
    public static readonly StringName RenameSymbol = nameof(RenameSymbol);
    public static readonly StringName CodeFixes = "CodeFixes";
    public static readonly StringName StepOver = "StepOver";
    public static readonly StringName FindInFiles = nameof(FindInFiles);
    public static readonly StringName FindFiles = nameof(FindFiles);
    public static readonly StringName SaveFile = nameof(SaveFile);
    public static readonly StringName SaveAllFiles = nameof(SaveAllFiles);
}

public static class ThemeStringNames
{
    public static readonly StringName FontColor = "font_color";
}