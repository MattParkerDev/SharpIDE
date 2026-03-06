using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private static readonly StringName ThemeInfoStringName = "ThemeInfo";
    private static readonly StringName IsLight1OrDark2StringName = "IsLight1OrDark2";
    private static readonly StringName CurrentLineColorStringName = "current_line_color";

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
        SyntaxHighlighter = null;
        SyntaxHighlighter = _syntaxHighlighter; // Reassign to trigger redraw
        MakeEditorTransparent();
        ApplyCurrentLineHighlightColor();
    }

    private void MakeEditorTransparent()
    {
        var codeEditStyle = (StyleBoxFlat)GetThemeStylebox("normal");
        var bgColor = codeEditStyle.BgColor;
        bgColor.A = 0f;
        codeEditStyle.BgColor = bgColor;
    }

    private Task OnBackgroundTransparencyChangedAsync(double transparency)
    {
        MakeEditorTransparent();
        return Task.CompletedTask;
    }

    private Task OnCodeBackgroundTransparencyChangedAsync(double transparency)
    {
        // Force the syntax highlighter to redraw itself
        var syntaxHighlighter = SyntaxHighlighter;
        SyntaxHighlighter = null;
        SyntaxHighlighter = syntaxHighlighter;
        return Task.CompletedTask;
    }

    private Task OnCurrentLineHighlightColorChangedAsync(Color color)
    {
        ApplyCurrentLineHighlightColor();
        return Task.CompletedTask;
    }

    private void ApplyCurrentLineHighlightColor()
    {
        var colorString = Singletons.AppState.IdeSettings.CurrentLineHighlightColor;
        var color = new Color(colorString);
        AddThemeColorOverride(CurrentLineColorStringName, color);
    }
}