using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public static class SetThemeExtensions
{
    private static readonly Theme LightTheme = ResourceLoader.Load<Theme>("uid://dc7l6bjhn61i5");
    private static readonly Color LightThemeClearColor = new Color("fdfdfd");
    private static readonly Theme DarkTheme = ResourceLoader.Load<Theme>("uid://epmt8kq6efrs");
    private static readonly Color DarkThemeClearColor = new Color("4d4d4d");

    public static Font EditorDefaultFont { get; set; } = null!;
    public static int EditorDefaultFontSize { get; set; } = -1;
    public static Font TerminalDefaultFont { get; set; } = null!;
    public static int TerminalDefaultFontSize { get; set; } = -1;

    public static void SetIdeTheme(this Node node, LightOrDarkTheme theme)
    {
        var rootWindow = node.GetTree().GetRoot();
        if (theme is LightOrDarkTheme.Light)
        {
            RenderingServer.Singleton.SetDefaultClearColor(LightThemeClearColor);
            rootWindow.Theme = LightTheme;
        }
        else if (theme is LightOrDarkTheme.Dark)
        {
            RenderingServer.Singleton.SetDefaultClearColor(DarkThemeClearColor);
            rootWindow.Theme = DarkTheme;
        }
    }
    
    public static void ThemeSetCodeEditFont(this Node node, Font font)
    {
        DarkTheme.SetFont(ThemeStringNames.Font, GodotNodeStringNames.CodeEdit, font);
        LightTheme.SetFont(ThemeStringNames.Font, GodotNodeStringNames.CodeEdit, font);
    }

    public static void ThemeSetCodeEditFontSize(this Node node, int fontSize)
    {
        LightTheme.SetFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.CodeEdit, fontSize);
        DarkTheme.SetFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.CodeEdit, fontSize);
    }
    
    public static void ThemeSetTerminalFont(this Node node, Font font)
    {
        DarkTheme.SetFont(ThemeStringNames.Terminal.NormalFont, GodotNodeStringNames.Terminal, font);
        DarkTheme.SetFont(ThemeStringNames.Terminal.BoldFont, GodotNodeStringNames.Terminal, font);
        DarkTheme.SetFont(ThemeStringNames.Terminal.ItalicsFont, GodotNodeStringNames.Terminal, font);
        DarkTheme.SetFont(ThemeStringNames.Terminal.BoldItalicsFont, GodotNodeStringNames.Terminal, font);
        
        LightTheme.SetFont(ThemeStringNames.Terminal.NormalFont, GodotNodeStringNames.Terminal, font);
        LightTheme.SetFont(ThemeStringNames.Terminal.BoldFont, GodotNodeStringNames.Terminal, font);
        LightTheme.SetFont(ThemeStringNames.Terminal.ItalicsFont, GodotNodeStringNames.Terminal, font);
        LightTheme.SetFont(ThemeStringNames.Terminal.BoldItalicsFont, GodotNodeStringNames.Terminal, font);
    }

    public static void ThemeSetTerminalFontSize(this Node node, int fontSize)
    {
        LightTheme.SetFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.Terminal, fontSize);
        DarkTheme.SetFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.Terminal, fontSize);
    }
}