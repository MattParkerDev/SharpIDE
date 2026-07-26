using Godot;

namespace SharpIDE.Godot.Features.Common;

public static partial class ThemeGenerator
{
    private static readonly ThemeColors DarkThemeColors = new();

    // Start with the dark palette so shared colors such as ANSI terminal colors only need to be declared once.
    private static readonly ThemeColors LightThemeColors = DarkThemeColors with
    {
        LowContrastBackground = new Color(0, 0, 0, 5f/255f), // black, 5/255 alpha
        MediumContrastBackground = new Color(0, 0, 0, 15f/255f), // black, 15/255 alpha
        LowContrastBorder = new Color(0f, 0f, 0f, 26f/255f), // black, 26/255 alpha
        HighContrastBackground = new Color(0f, 0f, 0f, 32f/255f), // black, 32/255 alpha
        Focus = new Color(0f, 0f, 0f, 0.75f),
        TransparentControlBackground = new Color(0.101960786f, 0.101960786f, 0.101960786f, 0.105882354f),
        PopupBackground = new Color(0.82f, 0.82f, 0.82f, 1f),
        PopupBorder = new Color(0.68f, 0.68f, 0.68f, 1f),
        CodeEditBorder = new Color(0f, 0f, 0f, 0.6f),
        Selection = new Color(0.6784314f, 0.8392157f, 1f, 1f),
        Label = new Color(0.17f, 0.17f, 0.17f, 1f),
        Sidebar = new Color(0.3f, 0.3f, 0.3f, 1f),
        SidebarActive = new Color(0.12f, 0.12f, 0.12f, 1f),
        NavigationIcon = new Color(0.22352941f, 0.22352941f, 0.22352941f, 1f),
        ButtonPressedBackground = new Color(0f, 0f, 0f, 32f/255f),
        ButtonPressedBorder = new Color(0f, 0f, 0f, 50f/255f), // black, 50/255 alpha
        CodeEditBackground = new Color(0.98f, 0.98f, 0.98f, 1f),
        SidebarHoverBackground = new Color(0f, 0f, 0f, 0.05882353f),
        LineEditBackground = new Color(0, 0, 0, 0.019607844f),
        NavigationNormalBackground = new Color(0f, 0f, 0f, 0.6f),
        NavigationHoverBackground = new Color(0.7413847f, 0.7413847f, 0.7413844f, 0.6f),
        NavigationPressedBackground = new Color(0.5609549f, 0.5609549f, 0.56095487f, 0.6f),
        PopupPanelBackground = new Color(0.82f, 0.82f, 0.82f, 1f),
        TreeSelectedBackground = new Color(0f, 0f, 0f, 32f / 255f),
        TreeHoveredBackground = new Color(0f, 0f, 0f, 15f / 255f),
        TreeCursorBackground = new Color(0f, 0f, 0f, 0f),
        TreeCursorBorder = new Color(0f, 0f, 0f, 48f / 255f),
        ScrollbarGrabberHighlight = new Color(0f, 0f, 0f, 0.2509804f),
        ScrollbarGrabberPressed = new Color(0f, 0f, 0f, 0.1882353f),
        WindowFrame = new Color(0.9079417f, 0.9079417f, 0.9079416f, 1f),
        WindowUnfocusedFrame = new Color(0.8047426f, 0.80474263f, 0.8047426f, 26f/255f),
        CompletionSelected = new Color(0.61f, 0.805f, 1f, 1f),
        CurrentLine = new Color(0.94f, 0.94f, 0.94f, 1f),
        SearchResult = new Color(0.5f, 0.5f, 0.5f, 0.15294118f),
        ControlFont = new Color(0.17f, 0.17f, 0.17f, 1f),
        Gray500 = new Color(0.49f, 0.49f, 0.49f, 1f),
        Gray600 = new Color(0.33f, 0.33f, 0.33f, 1f),
        Gray800 = new Color(0.1111969f, 0.11119682f, 0.11119684f, 1f),
        NavigationIconDisabled = new Color(0.7539839f, 0.75398386f, 0.75398386f, 1f),
        TerminalForeground = new Color(0.17f, 0.17f, 0.17f, 1f),
    };

    private sealed record ThemeColors
    {
        public Color LowContrastBackground { get; init; } = new(1f, 1f, 1f, 8f/255f); // white, 8/255 alpha
        public Color MediumContrastBackground { get; init; } = new(1f, 1f, 1f, 16f/255f); // white, 16/255 alpha
        public Color LowContrastBorder { get; init; } = new(1f, 1f, 1f, 26f/255f); // white, 26/255 alpha
        public Color HighContrastBackground { get; init; } = new(1f, 1f, 1f, 32f/255f); // white, 32/255 alpha
        public Color Focus { get; init; } = new(1f, 1f, 1f, 0.75f);
        public Color Transparent { get; init; } = new(0f, 0f, 0f, 0f);
        public Color TransparentControlBackground { get; init; } = new(0.1f, 0.1f, 0.1f, 0.6f);
        public Color PopupBackground { get; init; } = new(0.16862746f, 0.1764706f, 0.1882353f, 1f);
        public Color PopupBorder { get; init; } = new(0.24313726f, 0.2509804f, 0.27058825f, 1f);
        public Color CodeEditBorder { get; init; } = new(0.16470589f, 0.16862746f, 0.17254902f, 1f);
        public Color Selection { get; init; } = new(0.14117648f, 0.35686275f, 0.50980395f, 1f);
        public Color Label { get; init; } = new(0.83137256f, 0.83137256f, 0.83137256f, 1f);
        public Color Sidebar { get; init; } = new(0.54901963f, 0.54901963f, 0.54901963f, 1f);
        public Color SidebarActive { get; init; } = new(0.7490196f, 0.7490196f, 0.7490196f, 1f);
        public Color NavigationIcon { get; init; } = new(0.74509805f, 0.74509805f, 0.74509805f, 1f);
        public Color White { get; init; } = new(1f, 1f, 1f, 1f);
        public Color ButtonPressedBackground { get; init; } = new(0f, 0f, 0f, 50f/255f); // black, 50/255 alpha
        public Color ButtonPressedBorder { get; init; } = new(1f, 1f, 1f, 16f/255f); // white, 16/255 alpha
        public Color PopupShadow { get; init; } = new(0f, 0f, 0f, 0.5f);
        public Color CodeEditBackground { get; init; } = new(0.117647f, 0.117647f, 0.117647f, 1f);
        public Color SidebarHoverBackground { get; init; } = new(0.15540645f, 0.15904477f, 0.16268319f, 1f);
        public Color LineEditBackground { get; init; } = new(1f, 1f, 1f, 0.03137255f);
        public Color NavigationNormalBackground { get; init; } = new(0.5609549f, 0.5609549f, 0.56095487f, 0.6f);
        public Color NavigationHoverBackground { get; init; } = new(0.28235295f, 0.28235295f, 0.28235295f, 0.6f);
        public Color NavigationPressedBackground { get; init; } = new(0.09411765f, 0.09411765f, 0.09411765f, 0.6f);
        public Color PopupPanelBackground { get; init; } = new(0.1764706f, 0.1764706f, 0.1764706f, 1f);
        public Color WindowShadow { get; init; } = new(0f, 0f, 0f, 0.11764706f);
        public Color TerminalBorder { get; init; } = new(0f, 0f, 0f, 0.6f);
        public Color TreeSelectedBackground { get; init; } = new(0.14117648f, 0.35686275f, 0.50980395f, 1f);
        public Color TreeHoveredBackground { get; init; } = new(1f, 1f, 1f, 16f / 255f);
        public Color TreeCursorBackground { get; init; } = new(1f, 1f, 1f, 0.72156864f);
        public Color TreeCursorBorder { get; init; } = new(1f, 1f, 1f, 0.5686275f);
        public Color ScrollbarGrabberHighlight { get; init; } = new(1f, 1f, 1f, 0.2509804f);
        public Color ScrollbarGrabberPressed { get; init; } = new(1f, 1f, 1f, 0.1882353f);
        public Color WindowFrame { get; init; } = new(0.14767182f, 0.14767182f, 0.14767176f, 1f);
        public Color WindowUnfocusedFrame { get; init; } = new(0.10747979f, 0.107479714f, 0.10747973f, 1f);
        public Color CompletionBackground { get; init; } = new(1f, 1f, 1f, 0f);
        public Color CompletionSelected { get; init; } = new(0.18039216f, 0.2627451f, 0.43137255f, 1f);
        public Color CurrentLine { get; init; } = new(0.05882353f, 0.05882353f, 0.05882353f, 1f);
        public Color SearchResult { get; init; } = new(0.3f, 0.3f, 0.3f, 0.4117647f);
        public Color ControlFont { get; init; } = new(0.98039216f, 1f, 1f, 0.77254903f);
        public Color Gray500 { get; init; } = new(0.5137255f, 0.5137255f, 0.5137255f, 1f);
        public Color Gray600 { get; init; } = new(0.67058825f, 0.67058825f, 0.67058825f, 1f);
        public Color Gray800 { get; init; } = new(0.9137255f, 0.9137255f, 0.9137255f, 1f);
        public Color NavigationIconDisabled { get; init; } = new(0.45064795f, 0.45064837f, 0.45064825f, 1f);
        public Color Ansi0 { get; init; } = new(0f, 0f, 0f, 1f);
        public Color Ansi1 { get; init; } = new(0.803922f, 0f, 0f, 1f);
        public Color Ansi2 { get; init; } = new(0f, 0.803922f, 0f, 1f);
        public Color Ansi3 { get; init; } = new(0.803922f, 0.803922f, 0f, 1f);
        public Color Ansi4 { get; init; } = new(0f, 0f, 0.933333f, 1f);
        public Color Ansi5 { get; init; } = new(0.803922f, 0f, 0.803922f, 1f);
        public Color Ansi6 { get; init; } = new(0f, 0.803922f, 0.803922f, 1f);
        public Color Ansi7 { get; init; } = new(0.898039f, 0.898039f, 0.898039f, 1f);
        public Color Ansi8 { get; init; } = new(0.498039f, 0.498039f, 0.498039f, 1f);
        public Color Ansi9 { get; init; } = new(1f, 0f, 0f, 1f);
        public Color Ansi10 { get; init; } = new(0f, 1f, 0f, 1f);
        public Color Ansi11 { get; init; } = new(1f, 1f, 0f, 1f);
        public Color Ansi12 { get; init; } = new(0.360784f, 0.360784f, 1f, 1f);
        public Color Ansi13 { get; init; } = new(1f, 0f, 1f, 1f);
        public Color Ansi14 { get; init; } = new(0f, 1f, 1f, 1f);
        public Color TerminalForeground { get; init; } = new(0.875f, 0.875f, 0.875f, 1f);
    }
}
