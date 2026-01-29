using Godot;

namespace SharpIDE.Godot;

public static class InputStringNames
{
    public static readonly StringName Backspace = "ui_text_backspace";
    public static readonly StringName RenameSymbol = nameof(RenameSymbol);
    public static readonly StringName CodeFixes = nameof(CodeFixes);
    public static readonly StringName StepOver = nameof(StepOver);
    public static readonly StringName DebuggerStepIn = nameof(DebuggerStepIn);
    public static readonly StringName DebuggerStepOut = nameof(DebuggerStepOut);
    public static readonly StringName DebuggerContinue = nameof(DebuggerContinue);
    public static readonly StringName FindInFiles = nameof(FindInFiles);
    public static readonly StringName FindFiles = nameof(FindFiles);
    public static readonly StringName SaveFile = nameof(SaveFile);
    public static readonly StringName SaveAllFiles = nameof(SaveAllFiles);
    public static readonly StringName EditorFontSizeIncrease = nameof(EditorFontSizeIncrease);
    public static readonly StringName EditorFontSizeDecrease = nameof(EditorFontSizeDecrease);
    public static readonly StringName CodeEditorRequestCompletions = nameof(CodeEditorRequestCompletions);
}

public static class ThemeStringNames
{
    public static readonly StringName Font = "font";
    public static readonly StringName FontColor = "font_color";
    public static readonly StringName FontSize = "font_size";
    public static readonly StringName FontSelectedColor = "font_selected_color";
    public static readonly StringName FontHoveredColor = "font_hovered_color";
    public static readonly StringName FontHoveredSelectedColor = "font_hovered_selected_color";
    
    public static readonly StringName Panel = "panel";
    public static readonly StringName Separation = "separation";
    
    public static readonly StringName Completion = "completion";
    public static readonly StringName CompletionBackgroundColor = "completion_background_color";
    public static readonly StringName CompletionSelectedColor = "completion_selected_color";
    public static readonly StringName CompletionScrollHoveredColor = "completion_scroll_hovered_color";
    public static readonly StringName CompletionScrollColor = "completion_scroll_color";
    public static readonly StringName CompletionExistingColor = "completion_existing_color";
    public static readonly StringName CompletionColorBgIcon = "completion_color_bg";
}