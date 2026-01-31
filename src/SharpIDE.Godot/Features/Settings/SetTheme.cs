using Godot;

namespace SharpIDE.Godot.Features.Settings;

public static class SetTheme
{
    private static readonly Theme LightTheme = ResourceLoader.Load<Theme>("uid://dc7l6bjhn61i5");
    private static readonly Color LightThemeClearColor = new Color("fdfdfd");
    private static readonly Theme DarkTheme = ResourceLoader.Load<Theme>("uid://epmt8kq6efrs");
    private static readonly Color DarkThemeClearColor = new Color("4d4d4d");

    public static void SetIdeTheme(this Node node, string theme)
    {
        var rootWindow = node.GetTree().GetRoot();
        if (theme is "Light")
        {
            RenderingServer.Singleton.SetDefaultClearColor(LightThemeClearColor);
            rootWindow.Theme = LightTheme;
        }
        else if (theme is "Dark")
        {
            RenderingServer.Singleton.SetDefaultClearColor(DarkThemeClearColor);
            rootWindow.Theme = DarkTheme;
        }
    }
}