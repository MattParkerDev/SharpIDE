using System.Diagnostics;
using Godot;

namespace SharpIDE.Godot.Features.Common;

public static partial class ThemeGenerator
{
    private const string DarkThemePath = "res://Resources/DarkTheme.tres";
    private const string DarkThemeUid = "uid://epmt8kq6efrs";
    private const string LightThemePath = "res://Resources/LightTheme.tres";
    private const string LightThemeUid = "uid://dc7l6bjhn61i5";

    extension(Node node)
    {
        public void GenerateThemes()
        {
            var darkThemeSaved = SaveTheme(CreateTheme(DarkThemeColors, 2), DarkThemePath, DarkThemeUid);
            var lightThemeSaved = SaveTheme(CreateTheme(LightThemeColors, 1), LightThemePath, LightThemeUid);
            if (darkThemeSaved && lightThemeSaved)
            {
                GD.Print($"[ThemeGenerator] Saved themes to '{DarkThemePath}' and '{LightThemePath}'.");
            }

            Process.GetCurrentProcess().Kill();
        }
    }

    private static bool SaveTheme(Theme theme, string path, string uid)
    {
        theme.TakeOverPath(path);
        var error = ResourceSaver.Save(theme, path);
        if (error is not Error.Ok)
        {
            GD.PrintErr($"[ThemeGenerator] Failed to save theme to '{path}': {error}.");
            return false;
        }

        error = ResourceSaver.SetUid(path, ResourceUid.TextToId(uid));
        if (error is not Error.Ok)
        {
            GD.PrintErr($"[ThemeGenerator] Failed to set the UID for '{path}': {error}.");
            return false;
        }

        return true;
    }
}
