using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public static class SetTheme
{
    private static readonly Theme LightTheme = ResourceLoader.Load<Theme>("uid://dc7l6bjhn61i5");
    private static readonly Color LightThemeClearColor = new Color("fdfdfd");
    private static readonly Theme DarkTheme = ResourceLoader.Load<Theme>("uid://epmt8kq6efrs");
    private static readonly Color DarkThemeClearColor = new Color("4d4d4d");
    private static readonly Theme ExtraDarkTheme = ResourceLoader.Load<Theme>("uid://k1h5yus8dekc");
    private static readonly Color ExtraDarkThemeClearColor = new Color("#000000");

    public static void SetIdeTheme(this Node node, IdeTheme theme)
    {
        var rootWindow = node.GetTree().GetRoot();
        if (theme is IdeTheme.Light)
        {
            RenderingServer.Singleton.SetDefaultClearColor(LightThemeClearColor);
            rootWindow.Theme = LightTheme;
        }
        else if (theme is IdeTheme.Dark)
        {
            RenderingServer.Singleton.SetDefaultClearColor(DarkThemeClearColor);
            rootWindow.Theme = DarkTheme;
        }
        else if (theme is IdeTheme.ExtraDark)
        {
            RenderingServer.Singleton.SetDefaultClearColor(ExtraDarkThemeClearColor);
            rootWindow.Theme = ExtraDarkTheme;
        }
    }
}