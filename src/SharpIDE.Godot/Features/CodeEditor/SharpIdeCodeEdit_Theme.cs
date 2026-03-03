using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private void UpdateEditorThemeForCurrentTheme()
    {
        var ideTheme = Singletons.AppState.IdeSettings.Theme;
        UpdateEditorTheme(ideTheme);
    }
    
    // Only async for the EventWrapper subscription
    private Task UpdateEditorThemeAsync(IdeTheme theme)
    {
        UpdateEditorTheme(theme);
        return Task.CompletedTask;
    }
    
    private void UpdateEditorTheme(IdeTheme theme)
    {
        _syntaxHighlighter.UpdateThemeColorCache(theme);
        SyntaxHighlighter = null;
        SyntaxHighlighter = _syntaxHighlighter; // Reassign to trigger redraw
    }
}