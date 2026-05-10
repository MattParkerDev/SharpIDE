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
    private Task UpdateEditorThemeAsync(LightOrDarkTheme lightOrDarkTheme)
    {
        UpdateEditorTheme(lightOrDarkTheme);
        return Task.CompletedTask;
    }
    private void UpdateEditorTheme(LightOrDarkTheme lightOrDarkTheme)
    {
        _syntaxHighlighter.UpdateThemeColorCache(lightOrDarkTheme);
    }
}