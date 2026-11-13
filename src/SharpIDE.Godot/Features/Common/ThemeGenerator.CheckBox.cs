using System.Globalization;
using Godot;

namespace SharpIDE.Godot.Features.Common;

public static partial class ThemeGenerator
{
    /// <summary>
    /// A CheckBox otherwise takes the Button styles, as the theme defines no CheckBox ones - so no background, and the box drawn like a panel's, a step brighter
    /// </summary>
    private static void AddCheckBoxStyles(Theme theme, ThemeColors colors)
    {
        var checkBox_Empty = new StyleBoxEmpty
        {
            ResourceSceneUniqueId = "StyleBoxEmpty_chkbx",
            ContentMarginLeft = 4.0f,
            ContentMarginTop = 4.0f,
            ContentMarginRight = 4.0f,
            ContentMarginBottom = 4.0f
        };
        foreach (var style in (string[])["normal", "hover", "pressed", "hover_pressed", "disabled"])
        {
            theme.SetStylebox(style, "CheckBox", checkBox_Empty);
        }

        theme.SetIcon("unchecked", "CheckBox", CheckBoxIcon("DPITexture_chkunc", colors, ticked: false, disabled: false));
        theme.SetIcon("checked", "CheckBox", CheckBoxIcon("DPITexture_chkchk", colors, ticked: true, disabled: false));
        theme.SetIcon("unchecked_disabled", "CheckBox", CheckBoxIcon("DPITexture_chkund", colors, ticked: false, disabled: true));
        theme.SetIcon("checked_disabled", "CheckBox", CheckBoxIcon("DPITexture_chkchd", colors, ticked: true, disabled: true));
    }

    /// The 16px box Godot's own icons use, as an SVG so it stays sharp at any UI scale.
    /// Unchecked is a step up on a panel's fill and outline; checked is solid in the label colour, with the tick in the editor background colour
    private static DpiTexture CheckBoxIcon(string id, ThemeColors colors, bool ticked, bool disabled)
    {
        var opacity = disabled ? 0.5f : 1f;
        var box = ticked
            ? $"""<rect x="1" y="1" width="14" height="14" rx="3.5" {SvgPaint("fill", colors.Label, opacity)}/>"""
            : $"""<rect x="1.5" y="1.5" width="13" height="13" rx="3" stroke-width="1" {SvgPaint("fill", colors.MediumContrastBackground, opacity)} {SvgPaint("stroke", colors.HighContrastBackground, opacity)}/>""";
        var tick = ticked
            ? $"""<path d="M4.5 8.25 L7 10.75 L11.5 5.5" fill="none" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" {SvgPaint("stroke", colors.CodeEditBackground, 1f)}/>"""
            : string.Empty;
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16">{box}{tick}</svg>""";
        var texture = DpiTexture.CreateFromString(svg);
        texture.ResourceSceneUniqueId = id;
        return texture;
    }

    private static string SvgPaint(string attribute, Color color, float opacity) =>
        $"""{attribute}="#{color.ToHtml(false)}" {attribute}-opacity="{(color.A * opacity).ToString("0.###", CultureInfo.InvariantCulture)}" """.TrimEnd();
}
