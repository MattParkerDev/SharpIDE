using Godot;

namespace SharpIDE.Godot.Features.Common;

public static partial class ThemeGenerator
{
    private static Theme CreateTheme(ThemeColors colors, int lightOrDark)
    {
        var whiteAt3Percent = colors.LowContrastBackground;
        var whiteAt6Percent = colors.MediumContrastBackground;
        var whiteAt10Percent = colors.LowContrastBorder;
        var whiteAt12Percent = colors.HighContrastBackground;
        var whiteAt75Percent = colors.Focus;
        var transparent = colors.Transparent;
        var translucentDarkBackground = colors.TransparentControlBackground;
        var popupBackground = colors.PopupBackground;
        var popupBorder = colors.PopupBorder;
        var codeEditBorder = colors.CodeEditBorder;
        var selectionColor = colors.Selection;
        var lightLabelColor = colors.Label;
        var sidebarColor = colors.Sidebar;
        var sidebarActiveColor = colors.SidebarActive;
        var navigationIconColor = colors.NavigationIcon;
        var white = colors.White;

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

        var button_Hover = new StyleBoxFlat
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
        theme.SetStylebox("hover", "Button", button_Hover);

        var button_Normal = new StyleBoxFlat
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
        theme.SetStylebox("normal", "Button", button_Normal);

        var button_Pressed = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_5v2og",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.ButtonPressedBackground,
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
        theme.SetStylebox("pressed", "Button", button_Pressed);

        var codeEdit_Completion = new StyleBoxFlat
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
	        ShadowColor = colors.PopupShadow,
	        ShadowSize = 2
        };
        theme.SetStylebox("completion", "CodeEdit", codeEdit_Completion);

        var codeEdit_NormalAndReadOnly = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_82udi",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.CodeEditBackground,
	        DrawCenter = false,
	        BorderColor = codeEditBorder,
	        CornerRadiusTopLeft = 8,
	        CornerRadiusTopRight = 8,
	        CornerRadiusBottomRight = 8,
	        CornerRadiusBottomLeft = 8,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "CodeEdit", codeEdit_NormalAndReadOnly);
        theme.SetStylebox("read_only", "CodeEdit", codeEdit_NormalAndReadOnly);

        var codeEditorTabContainer_Panel = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_21uq4",
	        ContentMarginLeft = 1.0f,
	        ContentMarginTop = 0.0f,
	        ContentMarginRight = 1.0f,
	        ContentMarginBottom = 1.0f,
	        DrawCenter = false,
	        BorderColor = codeEditBorder
        };
        theme.SetStylebox("panel", "CodeEditorTabContainer", codeEditorTabContainer_Panel);

        var tab_Hovered = new StyleBoxFlat
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
        theme.SetStylebox("tab_hovered", "CodeEditorTabContainer", tab_Hovered);

        var tab_Selected = new StyleBoxFlat
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
        theme.SetStylebox("tab_selected", "CodeEditorTabContainer", tab_Selected);

        var tab_Unselected = new StyleBoxFlat
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
        theme.SetStylebox("tab_unselected", "CodeEditorTabContainer", tab_Unselected);

        var codeEditorTabContainer_TabBarBackground = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_5oigc",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = transparent,
	        DrawCenter = false
        };
        theme.SetStylebox("tabbar_background", "CodeEditorTabContainer", codeEditorTabContainer_TabBarBackground);

        var editorHoverPopupPanelContainer_Panel = new StyleBoxFlat
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
	        ShadowColor = colors.PopupShadow,
	        ShadowSize = 2
        };
        theme.SetStylebox("panel", "EditorHoverPopupPanelContainer", editorHoverPopupPanelContainer_Panel);

        var foldableContainer_TitleCollapsedPanel = new StyleBoxFlat
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
        theme.SetStylebox("title_collapsed_panel", "FoldableContainer", foldableContainer_TitleCollapsedPanel);

        var ideSidebarButton_HoverAndPressed = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_njudc",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.SidebarHoverBackground,
	        CornerRadiusTopLeft = 6,
	        CornerRadiusTopRight = 6,
	        CornerRadiusBottomRight = 6,
	        CornerRadiusBottomLeft = 6,
	        CornerDetail = 5
        };
        theme.SetStylebox("hover", "IdeSidebarButton", ideSidebarButton_HoverAndPressed);

        var ideSidebarButton_Normal = new StyleBoxFlat
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
        theme.SetStylebox("normal", "IdeSidebarButton", ideSidebarButton_Normal);
        theme.SetStylebox("pressed", "IdeSidebarButton", ideSidebarButton_HoverAndPressed);

        var lineEdit_Normal = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_4dj27",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.LineEditBackground,
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
        theme.SetStylebox("normal", "LineEdit", lineEdit_Normal);

        var navigationArrowButton_DisabledAndNormal = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_ch0dy",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.NavigationNormalBackground,
	        DrawCenter = false,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("disabled", "NavigationArrowButton", navigationArrowButton_DisabledAndNormal);

        var navigationArrowButton_Hover = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_al7qp",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.NavigationHoverBackground,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("hover", "NavigationArrowButton", navigationArrowButton_Hover);
        theme.SetStylebox("normal", "NavigationArrowButton", navigationArrowButton_DisabledAndNormal);

        var navigationArrowButton_Pressed = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_qn0n3",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.NavigationPressedBackground,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("pressed", "NavigationArrowButton", navigationArrowButton_Pressed);

        var panel_Panel = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_6t7l7",
	        BgColor = whiteAt3Percent,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = whiteAt10Percent
        };
        theme.SetStylebox("panel", "Panel", panel_Panel);

        var panelContainer_Panel = new StyleBoxFlat
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
        theme.SetStylebox("panel", "PanelContainer", panelContainer_Panel);

        var popupPanel_Panel = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_amw38",
	        BgColor = colors.PopupPanelBackground,
	        CornerRadiusTopLeft = 5,
	        CornerRadiusTopRight = 5,
	        CornerRadiusBottomRight = 5,
	        CornerRadiusBottomLeft = 5,
	        ShadowColor = colors.WindowShadow,
	        ShadowSize = 4
        };
        theme.SetStylebox("panel", "PopupPanel", popupPanel_Panel);
        theme.SetStylebox("tab_hovered", "TabBar", tab_Hovered);
        theme.SetStylebox("tab_selected", "TabBar", tab_Selected);
        theme.SetStylebox("tab_unselected", "TabBar", tab_Unselected);

        var terminal_Focus = new StyleBoxFlat
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
        theme.SetStylebox("focus", "Terminal", terminal_Focus);

        var terminal_Normal = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_5srvn",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = translucentDarkBackground,
	        DrawCenter = false,
	        BorderColor = colors.TerminalBorder,
	        CornerRadiusTopLeft = 3,
	        CornerRadiusTopRight = 3,
	        CornerRadiusBottomRight = 3,
	        CornerRadiusBottomLeft = 3,
	        CornerDetail = 5
        };
        theme.SetStylebox("normal", "Terminal", terminal_Normal);

        var tree_CursorAndCursorUnfocused = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_yqdk6",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.TreeCursorBackground,
	        DrawCenter = false,
	        BorderWidthLeft = 1,
	        BorderWidthTop = 1,
	        BorderWidthRight = 1,
	        BorderWidthBottom = 1,
	        BorderColor = colors.TreeCursorBorder,
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
        theme.SetStylebox("cursor", "Tree", tree_CursorAndCursorUnfocused);
        theme.SetStylebox("cursor_unfocused", "Tree", tree_CursorAndCursorUnfocused);

        var vScrollBar_Grabber = new StyleBoxFlat
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
        theme.SetStylebox("grabber", "VScrollBar", vScrollBar_Grabber);

        var vScrollBar_GrabberHighlight = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_vru7m",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.ScrollbarGrabberHighlight,
	        CornerRadiusTopLeft = 10,
	        CornerRadiusTopRight = 10,
	        CornerRadiusBottomRight = 10,
	        CornerRadiusBottomLeft = 10,
	        CornerDetail = 6
        };
        theme.SetStylebox("grabber_highlight", "VScrollBar", vScrollBar_GrabberHighlight);

        var vScrollBar_GrabberPressed = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_2wjv5",
	        ContentMarginLeft = 4.0f,
	        ContentMarginTop = 4.0f,
	        ContentMarginRight = 4.0f,
	        ContentMarginBottom = 4.0f,
	        BgColor = colors.ScrollbarGrabberPressed,
	        CornerRadiusTopLeft = 10,
	        CornerRadiusTopRight = 10,
	        CornerRadiusBottomRight = 10,
	        CornerRadiusBottomLeft = 10,
	        CornerDetail = 6
        };
        theme.SetStylebox("grabber_pressed", "VScrollBar", vScrollBar_GrabberPressed);

        var window_EmbeddedBorder = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_enfp6",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 28.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 8.0f,
	        BgColor = colors.WindowBackground,
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
        theme.SetStylebox("embedded_border", "Window", window_EmbeddedBorder);

        var window_EmbeddedUnfocusedBorder = new StyleBoxFlat
        {
	        ResourceSceneUniqueId = "StyleBoxFlat_kta15",
	        ContentMarginLeft = 10.0f,
	        ContentMarginTop = 28.0f,
	        ContentMarginRight = 10.0f,
	        ContentMarginBottom = 8.0f,
	        BgColor = colors.WindowUnfocusedBackground,
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
        theme.SetStylebox("embedded_unfocused_border", "Window", window_EmbeddedUnfocusedBorder);
        theme.SetColor("completion_background_color", "CodeEdit", colors.CompletionBackground);
        theme.SetColor("completion_selected_color", "CodeEdit", colors.CompletionSelected);
        theme.SetColor("current_line_color", "CodeEdit", colors.CurrentLine);
        theme.SetColor("font_readonly_color", "CodeEdit", white);
        theme.SetColor("search_result_border_color", "CodeEdit", selectionColor);
        theme.SetColor("search_result_color", "CodeEdit", colors.SearchResult);
        theme.SetColor("selection_color", "CodeEdit", selectionColor);
        theme.SetFont("font", "CodeEdit", ResourceLoader.Load<Font>("uid://cctwlwcoycek7"));
        theme.SetFontSize("font_size", "CodeEdit", 18);
        theme.SetColor("font_color", "Control", colors.ControlFont);
        theme.SetColor("collapsed_font_color", "FoldableContainer", lightLabelColor);
        theme.SetColor("font_color", "FoldableContainer", lightLabelColor);
        theme.SetColor("font_color", "Gray500Label", colors.Gray500);
        theme.SetColor("font_color", "Gray600Label", colors.Gray600);
        theme.SetColor("font_color", "Gray700Label", lightLabelColor);
        theme.SetColor("font_color", "Gray800Label", colors.Gray800);
        theme.SetColor("font_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("font_focus_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("font_hover_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("font_hover_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("font_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("icon_hover_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("icon_hover_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("icon_normal_color", "IdeSidebarButton", sidebarColor);
        theme.SetColor("icon_pressed_color", "IdeSidebarButton", sidebarActiveColor);
        theme.SetColor("icon_disabled_color", "NavigationArrowButton", colors.NavigationIconDisabled);
        theme.SetColor("icon_hover_color", "NavigationArrowButton", navigationIconColor);
        theme.SetColor("icon_hover_pressed_color", "NavigationArrowButton", navigationIconColor);
        theme.SetColor("icon_normal_color", "NavigationArrowButton", navigationIconColor);
        theme.SetColor("icon_pressed_color", "NavigationArrowButton", navigationIconColor);
        theme.SetIcon("close", "TabBar", ResourceLoader.Load<Texture2D>("uid://d0wy2vggrfgdh"));
        theme.SetColor("ansi_0_color", "Terminal", colors.Ansi0);
        theme.SetColor("ansi_10_color", "Terminal", colors.Ansi10);
        theme.SetColor("ansi_11_color", "Terminal", colors.Ansi11);
        theme.SetColor("ansi_12_color", "Terminal", colors.Ansi12);
        theme.SetColor("ansi_13_color", "Terminal", colors.Ansi13);
        theme.SetColor("ansi_14_color", "Terminal", colors.Ansi14);
        theme.SetColor("ansi_15_color", "Terminal", white);
        theme.SetColor("ansi_1_color", "Terminal", colors.Ansi1);
        theme.SetColor("ansi_2_color", "Terminal", colors.Ansi2);
        theme.SetColor("ansi_3_color", "Terminal", colors.Ansi3);
        theme.SetColor("ansi_4_color", "Terminal", colors.Ansi4);
        theme.SetColor("ansi_5_color", "Terminal", colors.Ansi5);
        theme.SetColor("ansi_6_color", "Terminal", colors.Ansi6);
        theme.SetColor("ansi_7_color", "Terminal", colors.Ansi7);
        theme.SetColor("ansi_8_color", "Terminal", colors.Ansi8);
        theme.SetColor("ansi_9_color", "Terminal", colors.Ansi9);
        theme.SetColor("background_color", "Terminal", transparent);
        theme.SetColor("foreground_color", "Terminal", colors.TerminalForeground);
        theme.SetFont("bold_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFont("bold_italics_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFont("italics_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFont("normal_font", "Terminal", ResourceLoader.Load<Font>("uid://vmgmcu8gc6nt"));
        theme.SetFontSize("font_size", "Terminal", 16);
        theme.SetConstant("IsLight1OrDark2", "ThemeInfo", lightOrDark);

        return theme;
    }
}
