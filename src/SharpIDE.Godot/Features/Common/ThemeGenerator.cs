using System.Diagnostics;
using Godot;

namespace SharpIDE.Godot.Features.Common;

public static partial class ThemeGenerator
{
    private const string DarkThemePath = "res://Resources/DarkTheme.tres";
    private const string DarkThemeUid = "uid://epmt8kq6efrs";

    extension(Node node)
    {
        public void GenerateDarkTheme()
        {
            var theme = CreateDarkTheme();
            theme.TakeOverPath(DarkThemePath);
            var error = ResourceSaver.Save(theme, DarkThemePath);
            if (error is not Error.Ok)
            {
                GD.PrintErr($"[ThemeGenerator] Failed to save dark theme to '{DarkThemePath}': {error}.");
                return;
            }

            ResourceSaver.SetUid(DarkThemePath, ResourceUid.TextToId(DarkThemeUid));
            GD.Print($"[ThemeGenerator] Saved dark theme to '{DarkThemePath}'.");
            Process.GetCurrentProcess().Kill();
        }
    }
}
