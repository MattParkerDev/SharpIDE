using Godot;
using SharpIDE.Godot.Features.CodeEditor.TextMate;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private static readonly StringName ThemeInfoStringName = "ThemeInfo";
    private static readonly StringName IsLight1OrDark2StringName = "IsLight1OrDark2";

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
        var customThemePath = Singletons.AppState.IdeSettings.CustomThemePath;

        if (!string.IsNullOrEmpty(customThemePath) && File.Exists(customThemePath))
        {
            try
            {
                var tmTheme = TextMateThemeParser.ParseFromFile(customThemePath);
                var fallbackColorSet = lightOrDarkTheme == LightOrDarkTheme.Light
                    ? EditorThemeColours.Light
                    : EditorThemeColours.Dark;
                var customColorSet = TextMateEditorThemeColorSetBuilder.Build(tmTheme, fallbackColorSet);
                _syntaxHighlighter.ColourSetForTheme = customColorSet;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Failed to load custom theme: {ex.Message}. Falling back to built-in theme.");
                _syntaxHighlighter.UpdateThemeColorCache(lightOrDarkTheme);
            }
        }
        else
        {
            _syntaxHighlighter.UpdateThemeColorCache(lightOrDarkTheme);
        }

        if (_usingGrammarHighlighter)
        {
            RecolorizeWithGrammar(Text);
            return;
        }

        SyntaxHighlighter = null;
        SyntaxHighlighter = _syntaxHighlighter; // Reassign to trigger redraw
    }
}