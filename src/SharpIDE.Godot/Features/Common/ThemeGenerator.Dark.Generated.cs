using Godot;

namespace SharpIDE.Godot.Features.Common;

public static partial class ThemeGenerator
{
    private static Theme CreateDarkTheme()
    {
        var whiteAt3Percent = new Color(1f, 1f, 1f, 0.030000001f);
        var whiteAt6Percent = new Color(1f, 1f, 1f, 0.0627451f);
        var whiteAt10Percent = new Color(1f, 1f, 1f, 0.101960786f);
        var whiteAt12Percent = new Color(1f, 1f, 1f, 0.1254902f);
        var whiteAt75Percent = new Color(1f, 1f, 1f, 0.75f);
        var transparent = new Color(0f, 0f, 0f, 0f);
        var translucentDarkBackground = new Color(0.1f, 0.1f, 0.1f, 0.6f);
        var popupBackground = new Color(0.16862746f, 0.1764706f, 0.1882353f, 1f);
        var popupBorder = new Color(0.24313726f, 0.2509804f, 0.27058825f, 1f);
        var codeEditBorder = new Color(0.16470589f, 0.16862746f, 0.17254902f, 1f);
        var selectionColor = new Color(0.14117648f, 0.35686275f, 0.50980395f, 1f);
        var lightLabelColor = new Color(0.83137256f, 0.83137256f, 0.83137256f, 1f);
        var sidebarColor = new Color(0.54901963f, 0.54901963f, 0.54901963f, 1f);
        var sidebarActiveColor = new Color(0.7490196f, 0.7490196f, 0.7490196f, 1f);
        var navigationIconColor = new Color(0.74509805f, 0.74509805f, 0.74509805f, 1f);
        var white = new Color(1f, 1f, 1f, 1f);

        var theme = new Theme();
        theme.DefaultFont = ResourceLoader.Load<Font>("uid://38igu11xwba6");
        theme.SetTypeVariation("CodeEditorTabContainer", "TabContainer");
        theme.SetTypeVariation("EditorHoverPopupPanelContainer", "PanelContainer");
        theme.SetTypeVariation("Gray500Label", "Label");
        theme.SetTypeVariation("Gray600Label", "Label");
        theme.SetTypeVariation("Gray700Label", "Label");
        theme.SetTypeVariation("Gray800Label", "Label");
        theme.SetTypeVariation("IdeSidebarButton", "Button");
        theme.SetTypeVariation("NavigationArrowButton", "Button");

        var styleBox0 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_dwjdr",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = whiteAt6Percent,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt12Percent,
	        BorderBlend = true,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("hover", "Button", styleBox0);

        var styleBox1 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_kyxro",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = whiteAt3Percent,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt10Percent,
	        BorderBlend = true,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "Button", styleBox1);

        var styleBox2 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_5v2og",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(0f, 0f, 0f, 0.19607843f),
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt6Percent,
	        BorderBlend = true,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("pressed", "Button", styleBox2);

        var styleBox3 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_38n5o",
	        BgColor = popupBackground,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = popupBorder,
	        CornerRadiusTopLeft = 4,
	        CornerRadiusTopRight = 4,
	        CornerRadiusBottomRight = 4,
	        CornerRadiusBottomLeft = 4,
	        ExpandMarginLeft = -2.0f,
	        ExpandMarginTop = -2.0f,
	        ExpandMarginRight = -2.0f,
	        ExpandMarginBottom = -2.0f,
	        ShadowColor = new Color(0f, 0f, 0f, 0.5f),
	        ShadowSize = 2
        };
        theme.SetStylebox("completion", "CodeEdit", styleBox3);

        var styleBox4 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_82udi",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(0.117647f, 0.117647f, 0.117647f, 1f),
	        DrawCenter = false,
	        BorderColor = codeEditBorder,
	        CornerRadiusTopLeft = 8,
	        CornerRadiusTopRight = 8,
	        CornerRadiusBottomRight = 8,
	        CornerRadiusBottomLeft = 8,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "CodeEdit", styleBox4);
        theme.SetStylebox("read_only", "CodeEdit", styleBox4);

        var styleBox5 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_21uq4",
	        ContentMarginLeft = 1.0f,
	        ContentMarginTop = 0.0f,
	        ContentMarginRight = 1.0f,
	        ContentMarginBottom = 1.0f,
	        DrawCenter = false,
	        BorderColor = codeEditBorder
        };
        theme.SetStylebox("panel", "CodeEditorTabContainer", styleBox5);

        var styleBox6 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_an00s",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = whiteAt12Percent,
	        BorderColor = whiteAt75Percent,
	        CornerRadiusTopLeft = 6,
	        CornerRadiusTopRight = 6,
	        CornerRadiusBottomRight = 6,
	        CornerRadiusBottomLeft = 6,
	        CornerDetail = 5
        };
        theme.SetStylebox("tab_hovered", "CodeEditorTabContainer", styleBox6);

        var styleBox7 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_ua1vr",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = whiteAt12Percent,
	        BorderColor = whiteAt75Percent,
	        CornerRadiusTopLeft = 6,
	        CornerRadiusTopRight = 6,
	        CornerRadiusBottomRight = 6,
	        CornerRadiusBottomLeft = 6,
	        CornerDetail = 5
        };
        theme.SetStylebox("tab_selected", "CodeEditorTabContainer", styleBox7);

        var styleBox8 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_ml8q5",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = transparent,
	        DrawCenter = false,
	        CornerRadiusTopLeft = 6,
	        CornerRadiusTopRight = 6,
	        CornerRadiusBottomRight = 6,
	        CornerRadiusBottomLeft = 6,
	        CornerDetail = 5
        };
        theme.SetStylebox("tab_unselected", "CodeEditorTabContainer", styleBox8);

        var styleBox9 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_5oigc",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = transparent,
	        DrawCenter = false
        };
        theme.SetStylebox("tabbar_background", "CodeEditorTabContainer", styleBox9);

        var styleBox10 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_dvhtt",
	        ContentMarginLeft = 12.0f,
	        ContentMarginTop = 10.0f,
	        ContentMarginRight = 12.0f,
	        ContentMarginBottom = 10.0f,
	        BgColor = popupBackground,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = popupBorder,
	        CornerRadiusTopLeft = 4,
	        CornerRadiusTopRight = 4,
	        CornerRadiusBottomRight = 4,
	        CornerRadiusBottomLeft = 4,
	        ExpandMarginLeft = -2.0f,
	        ExpandMarginTop = -2.0f,
	        ExpandMarginRight = -2.0f,
	        ExpandMarginBottom = -2.0f,
	        ShadowColor = new Color(0f, 0f, 0f, 0.5019608f),
	        ShadowSize = 2
        };
        theme.SetStylebox("panel", "EditorHoverPopupPanelContainer", styleBox10);

        var styleBox11 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_hrgw7",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = translucentDarkBackground,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("title_collapsed_panel", "FoldableContainer", styleBox11);

        var styleBox12 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_njudc",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(0.15540645f, 0.15904477f, 0.16268319f, 1f),
	        CornerRadiusTopLeft = 6,
	        CornerRadiusTopRight = 6,
	        CornerRadiusBottomRight = 6,
	        CornerRadiusBottomLeft = 6,
	        CornerDetail = 5
        };
        theme.SetStylebox("hover", "IdeSidebarButton", styleBox12);

        var styleBox13 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_dsk6k",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = translucentDarkBackground,
	        DrawCenter = false,
	        CornerRadiusTopLeft = 6,
	        CornerRadiusTopRight = 6,
	        CornerRadiusBottomRight = 6,
	        CornerRadiusBottomLeft = 6,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "IdeSidebarButton", styleBox13);
        theme.SetStylebox("pressed", "IdeSidebarButton", styleBox12);

        var styleBox14 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_4dj27",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(1f, 1f, 1f, 0.03137255f),
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt10Percent,
	        BorderBlend = true,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "LineEdit", styleBox14);

        var styleBox15 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_ch0dy",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(0.5609549f, 0.5609549f, 0.56095487f, 0.6f),
	        DrawCenter = false,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("disabled", "NavigationArrowButton", styleBox15);

        var styleBox16 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_al7qp",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(0.28235295f, 0.28235295f, 0.28235295f, 0.6f),
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("hover", "NavigationArrowButton", styleBox16);
        theme.SetStylebox("normal", "NavigationArrowButton", styleBox15);

        var styleBox17 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_qn0n3",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(0.09411765f, 0.09411765f, 0.09411765f, 0.6f),
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("pressed", "NavigationArrowButton", styleBox17);

        var styleBox18 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_6t7l7",
	        BgColor = whiteAt3Percent,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt10Percent
        };
        theme.SetStylebox("panel", "Panel", styleBox18);

        var styleBox19 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_6e8is",
	        ContentMarginLeft = 0.0f,
	        ContentMarginTop = 0.0f,
	        ContentMarginRight = 0.0f,
	        ContentMarginBottom = 0.0f,
	        BgColor = whiteAt3Percent,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt10Percent,
	        CornerRadiusTopLeft = 8,
	        CornerRadiusTopRight = 8,
	        CornerRadiusBottomRight = 8,
	        CornerRadiusBottomLeft = 8
        };
        theme.SetStylebox("panel", "PanelContainer", styleBox19);

        var styleBox20 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_amw38",
	        BgColor = new Color(0.1764706f, 0.1764706f, 0.1764706f, 1f),
	        CornerRadiusTopLeft = 5,
	        CornerRadiusTopRight = 5,
	        CornerRadiusBottomRight = 5,
	        CornerRadiusBottomLeft = 5,
	        ShadowColor = new Color(0f, 0f, 0f, 0.11764706f),
	        ShadowSize = 4
        };
        theme.SetStylebox("panel", "PopupPanel", styleBox20);
        theme.SetStylebox("tab_hovered", "TabBar", styleBox6);
        theme.SetStylebox("tab_selected", "TabBar", styleBox7);
        theme.SetStylebox("tab_unselected", "TabBar", styleBox8);

        var styleBox21 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_bk23l",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = whiteAt75Percent,
	        DrawCenter = false,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5,
	        ExpandMarginLeft = 2.0f,
	        ExpandMarginTop = 2.0f,
	        ExpandMarginRight = 2.0f,
	        ExpandMarginBottom = 2.0f
        };
        theme.SetStylebox("focus", "Terminal", styleBox21);

        var styleBox22 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_5srvn",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = translucentDarkBackground,
	        DrawCenter = false,
	        BorderColor = new Color(0f, 0f, 0f, 0.6f),
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "Terminal", styleBox22);

        var styleBox23 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_yqdk6",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(1f, 1f, 1f, 0.72156864f),
	        DrawCenter = false,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = new Color(1f, 1f, 1f, 0.5686275f),
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5,
	        ExpandMarginLeft = 1.0f,
	        ExpandMarginTop = 1.0f,
	        ExpandMarginRight = 1.0f,
	        ExpandMarginBottom = 1.0f
        };
        theme.SetStylebox("cursor", "Tree", styleBox23);
        theme.SetStylebox("cursor_unfocused", "Tree", styleBox23);

        var styleBox24 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_un4ka",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = whiteAt12Percent,
	        CornerRadiusTopLeft = 10,
	        CornerRadiusTopRight = 10,
	        CornerRadiusBottomRight = 10,
	        CornerRadiusBottomLeft = 10,
	        CornerDetail = 6
        };
        theme.SetStylebox("grabber", "VScrollBar", styleBox24);

        var styleBox25 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_vru7m",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(1f, 1f, 1f, 0.2509804f),
	        CornerRadiusTopLeft = 10,
	        CornerRadiusTopRight = 10,
	        CornerRadiusBottomRight = 10,
	        CornerRadiusBottomLeft = 10,
	        CornerDetail = 6
        };
        theme.SetStylebox("grabber_highlight", "VScrollBar", styleBox25);

        var styleBox26 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_2wjv5",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = new Color(1f, 1f, 1f, 0.1882353f),
	        CornerRadiusTopLeft = 10,
	        CornerRadiusTopRight = 10,
	        CornerRadiusBottomRight = 10,
	        CornerRadiusBottomLeft = 10,
	        CornerDetail = 6
        };
        theme.SetStylebox("grabber_pressed", "VScrollBar", styleBox26);

        var styleBox27 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_enfp6",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 28.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 8.0f,
	        BgColor = new Color(0.14767182f, 0.14767182f, 0.14767176f, 1f),
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5,
	        ExpandMarginLeft = 8.0f,
	        ExpandMarginTop = 32.0f,
	        ExpandMarginRight = 8.0f,
	        ExpandMarginBottom = 6.0f
        };
        theme.SetStylebox("embedded_border", "Window", styleBox27);

        var styleBox28 = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_kta15",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 28.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 8.0f,
	        BgColor = new Color(0.10747979f, 0.107479714f, 0.10747973f, 1f),
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5,
	        ExpandMarginLeft = 8.0f,
	        ExpandMarginTop = 32.0f,
	        ExpandMarginRight = 8.0f,
	        ExpandMarginBottom = 6.0f
        };
        theme.SetStylebox("embedded_unfocused_border", "Window", styleBox28);
        theme.SetColor("completion_background_color", "CodeEdit", new Color(1f, 1f, 1f, 0f));
        theme.SetColor("completion_selected_color", "CodeEdit", new Color(0.18039216f, 0.2627451f, 0.43137255f, 1f));
        theme.SetColor("current_line_color", "CodeEdit", new Color(0.05882353f, 0.05882353f, 0.05882353f, 1f));
        theme.SetColor("font_readonly_color", "CodeEdit", white);
        theme.SetColor("search_result_border_color", "CodeEdit", selectionColor);
        theme.SetColor("search_result_color", "CodeEdit", new Color(0.3f, 0.3f, 0.3f, 0.4117647f));
        theme.SetColor("selection_color", "CodeEdit", selectionColor);
        theme.SetFont("font", "CodeEdit", ResourceLoader.Load<Font>("uid://cctwlwcoycek7"));
        theme.SetFontSize("font_size", "CodeEdit", 18);
        theme.SetColor("font_color", "Control", new Color(0.98039216f, 1f, 1f, 0.77254903f));
        theme.SetColor("collapsed_font_color", "FoldableContainer", lightLabelColor);
        theme.SetColor("font_color", "FoldableContainer", lightLabelColor);
        theme.SetColor("font_color", "Gray500Label", new Color(0.5137255f, 0.5137255f, 0.5137255f, 1f));
        theme.SetColor("font_color", "Gray600Label", new Color(0.67058825f, 0.67058825f, 0.67058825f, 1f));
        theme.SetColor("font_color", "Gray700Label", lightLabelColor);
        theme.SetColor("font_color", "Gray800Label", new Color(0.9137255f, 0.9137255f, 0.9137255f, 1f));
        theme.SetColor("font_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("font_focus_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("font_hover_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("font_hover_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("font_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("icon_hover_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("icon_hover_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("icon_normal_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("icon_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("icon_disabled_color", "NavigationArrowButton", new Color(0.45064795f, 0.45064837f, 0.45064825f, 1f));
        theme.SetColor("icon_hover_color", "NavigationArrowButton", navigationIconColor);
        theme.SetColor("icon_hover_pressed_color", "NavigationArrowButton", navigationIconColor);
        theme.SetColor("icon_normal_color", "NavigationArrowButton", navigationIconColor);
        theme.SetColor("icon_pressed_color", "NavigationArrowButton", navigationIconColor);
        theme.SetIcon("close", "TabBar", ResourceLoader.Load<Texture2D>("uid://d0wy2vggrfgdh"));
        theme.SetColor("ansi_0_color", "Terminal", new Color(0f, 0f, 0f, 1f));
        theme.SetColor("ansi_10_color", "Terminal", new Color(0f, 1f, 0f, 1f));
        theme.SetColor("ansi_11_color", "Terminal", new Color(1f, 1f, 0f, 1f));
        theme.SetColor("ansi_12_color", "Terminal", new Color(0.360784f, 0.360784f, 1f, 1f));
        theme.SetColor("ansi_13_color", "Terminal", new Color(1f, 0f, 1f, 1f));
        theme.SetColor("ansi_14_color", "Terminal", new Color(0f, 1f, 1f, 1f));
        theme.SetColor("ansi_15_color", "Terminal", white);
        theme.SetColor("ansi_1_color", "Terminal", new Color(0.803922f, 0f, 0f, 1f));
        theme.SetColor("ansi_2_color", "Terminal", new Color(0f, 0.803922f, 0f, 1f));
        theme.SetColor("ansi_3_color", "Terminal", new Color(0.803922f, 0.803922f, 0f, 1f));
        theme.SetColor("ansi_4_color", "Terminal", new Color(0f, 0f, 0.933333f, 1f));
        theme.SetColor("ansi_5_color", "Terminal", new Color(0.803922f, 0f, 0.803922f, 1f));
        theme.SetColor("ansi_6_color", "Terminal", new Color(0f, 0.803922f, 0.803922f, 1f));
        theme.SetColor("ansi_7_color", "Terminal", new Color(0.898039f, 0.898039f, 0.898039f, 1f));
        theme.SetColor("ansi_8_color", "Terminal", new Color(0.498039f, 0.498039f, 0.498039f, 1f));
        theme.SetColor("ansi_9_color", "Terminal", new Color(1f, 0f, 0f, 1f));
        theme.SetColor("background_color", "Terminal", transparent);
        theme.SetColor("foreground_color", "Terminal", new Color(0.875f, 0.875f, 0.875f, 1f));
        theme.SetFont("bold_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFont("bold_italics_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFont("italics_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFont("normal_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFontSize("font_size", "Terminal", 16);
        theme.SetConstant("IsLight1OrDark2", "ThemeInfo", 2);

        return theme;
    }
}
