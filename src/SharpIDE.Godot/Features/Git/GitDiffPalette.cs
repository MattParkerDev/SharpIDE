using Godot;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Godot.Features.Git;

internal static class GitDiffPalette
{
    public static readonly Color PanelBackgroundColor = new("171b22");
    public static readonly Color PanelBorderColor = new("343a46");
    public static readonly Color HeaderTextColor = new("d6dce8");
    public static readonly Color SubtleTextColor = new("8c95a6");
    public static readonly Color GutterBackgroundColor = new Color("1d222b", 0f);
    public static readonly Color GutterDividerColor = new("4a5363");
    public static readonly Color GutterDividerHoverColor = new("8ea2bf");
    public static readonly Color SelectionMarkerColor = new("6b7485");

    private static readonly Color AddedLineColor = new("294434");
    private static readonly Color RemovedLineColor = new("4a2e33");
    private static readonly Color ModifiedLeftLineColor = new("34445e");
    private static readonly Color ModifiedRightLineColor = new("34445e");
    private static readonly Color StagedAddedLineColor = new("3b6a4c");
    private static readonly Color StagedRemovedLineColor = new("6b3f46");
    private static readonly Color StagedModifiedLeftLineColor = new("45608a");
    private static readonly Color StagedModifiedRightLineColor = new("45608a");

    public static Color GetLineBackground(GitDiffDisplayRowKind kind, bool isLeftSide, bool isStaged = false)
    {
        return kind switch
        {
            GitDiffDisplayRowKind.Added => isLeftSide ? Colors.Transparent : isStaged ? StagedAddedLineColor : AddedLineColor,
            GitDiffDisplayRowKind.Removed => isLeftSide ? isStaged ? StagedRemovedLineColor : RemovedLineColor : Colors.Transparent,
            GitDiffDisplayRowKind.ModifiedLeft => isStaged ? StagedModifiedLeftLineColor : ModifiedLeftLineColor,
            GitDiffDisplayRowKind.ModifiedRight => isLeftSide
                ? isStaged ? StagedModifiedLeftLineColor : ModifiedLeftLineColor
                : isStaged ? StagedModifiedRightLineColor : ModifiedRightLineColor,
            _ => Colors.Transparent
        };
    }

    public static Color GetInlineBackground(GitInlineHighlightKind kind, bool isStaged = false)
    {
        return kind switch
        {
            GitInlineHighlightKind.Added => isStaged ? new Color("88d0a0", 0.78f) : new Color("78c08f", 0.76f),
            GitInlineHighlightKind.Removed => isStaged ? new Color("dea0a7", 0.78f) : new Color("ce9098", 0.76f),
            _ => isStaged ? new Color("9db7ea", 0.82f) : new Color("8ea9de", 0.8f)
        };
    }

    public static Color GetChunkBandColor(GitDiffChunkBackgroundKind kind, bool isStaged = false)
    {
        return kind switch
        {
            GitDiffChunkBackgroundKind.Added => isStaged ? new Color("3a654b", 0.86f) : new Color("2f4d3a", 0.78f),
            GitDiffChunkBackgroundKind.Removed => isStaged ? new Color("6a434a", 0.86f) : new Color("50343a", 0.78f),
            GitDiffChunkBackgroundKind.Modified => isStaged ? new Color("445f85", 0.9f) : new Color("334861", 0.8f),
            _ => new Color(GutterBackgroundColor, 0f)
        };
    }

    public static Color GetChunkAccentColor(GitDiffChunkBackgroundKind kind)
    {
        return kind switch
        {
            GitDiffChunkBackgroundKind.Added => new("6eb27b"),
            GitDiffChunkBackgroundKind.Removed => new("d17c7c"),
            GitDiffChunkBackgroundKind.Modified => new("7fa3d8"),
            _ => SelectionMarkerColor
        };
    }

    public static Color GetGutterBandColor(GitDiffChunkBackgroundKind kind, bool isStaged = false)
    {
        return kind switch
        {
            GitDiffChunkBackgroundKind.Added => isStaged ? new Color("3a654b", 0.9f) : GetChunkBandColor(kind, isStaged: false),
            GitDiffChunkBackgroundKind.Removed => isStaged ? new Color("6a434a", 0.9f) : GetChunkBandColor(kind, isStaged: false),
            GitDiffChunkBackgroundKind.Modified => isStaged ? new Color("445f85", 0.94f) : GetChunkBandColor(kind, isStaged: false),
            _ => GetChunkBandColor(kind, isStaged: false)
        };
    }

    public static Color GetGutterFillColor(GitDiffChunkBackgroundKind kind, bool isStaged = false, float opacityScale = 1f)
    {
        var baseColor = GetGutterBandColor(kind, isStaged);
        return new Color(baseColor, baseColor.A * opacityScale);
    }

    public static Color GetGutterGlowColor(GitDiffChunkBackgroundKind kind, bool isStaged = false, float opacityScale = 1f)
    {
        var alpha = kind switch
        {
            GitDiffChunkBackgroundKind.Added => isStaged ? 0.2f : 0.08f,
            GitDiffChunkBackgroundKind.Removed => isStaged ? 0.2f : 0.08f,
            GitDiffChunkBackgroundKind.Modified => isStaged ? 0.24f : 0.1f,
            _ => 0f
        };
        return new Color(GetChunkAccentColor(kind), alpha * opacityScale);
    }

    public static Color GetGutterStrokeColor(GitDiffChunkBackgroundKind kind, bool isStaged = false, float opacityScale = 1f)
    {
        var alpha = kind switch
        {
            GitDiffChunkBackgroundKind.Added => isStaged ? 1f : 0.78f,
            GitDiffChunkBackgroundKind.Removed => isStaged ? 1f : 0.78f,
            GitDiffChunkBackgroundKind.Modified => isStaged ? 1f : 0.82f,
            _ => 0f
        };
        return new Color(GetChunkAccentColor(kind), alpha * opacityScale);
    }

    public static Color GetConnectorFillColor(GitDiffChunkBackgroundKind kind, bool isStaged = false, float opacityScale = 1f)
    {
        var baseColor = GetChunkBandColor(kind, isStaged);
        return new Color(baseColor, baseColor.A * opacityScale);
    }

    public static Color GetConnectorStrokeColor(GitDiffChunkBackgroundKind kind, bool isStaged = false, float opacityScale = 1f)
    {
        var alpha = kind switch
        {
            GitDiffChunkBackgroundKind.Added => isStaged ? 0.92f : 0.78f,
            GitDiffChunkBackgroundKind.Removed => isStaged ? 0.92f : 0.78f,
            GitDiffChunkBackgroundKind.Modified => isStaged ? 0.96f : 0.82f,
            _ => 0f
        };
        return new Color(GetChunkAccentColor(kind), alpha * opacityScale);
    }

    public static Color GetConnectorGlowColor(GitDiffChunkBackgroundKind kind, bool isStaged = false, float opacityScale = 1f)
    {
        var alpha = kind switch
        {
            GitDiffChunkBackgroundKind.Added => isStaged ? 0.16f : 0.08f,
            GitDiffChunkBackgroundKind.Removed => isStaged ? 0.16f : 0.08f,
            GitDiffChunkBackgroundKind.Modified => isStaged ? 0.2f : 0.1f,
            _ => 0f
        };
        return new Color(GetChunkAccentColor(kind), alpha * opacityScale);
    }

    public static Color GetRowMarkerColor(GitDiffDisplayRowKind kind, bool isStaged = false)
    {
        return kind switch
        {
            GitDiffDisplayRowKind.Added => isStaged ? new Color("7fe892") : new Color("69ad76"),
            GitDiffDisplayRowKind.Removed => isStaged ? new Color("f09393") : new Color("d77b80"),
            GitDiffDisplayRowKind.ModifiedLeft or GitDiffDisplayRowKind.ModifiedRight => isStaged ? new Color("9bc3ff") : new Color("89acd9"),
            _ => SelectionMarkerColor
        };
    }

    public static StyleBoxFlat CreateEditorPanelStyle(bool drawLeftBorder = true, bool drawRightBorder = true)
    {
        return new StyleBoxFlat
        {
            BgColor = PanelBackgroundColor,
            BorderColor = PanelBorderColor,
            BorderWidthLeft = drawLeftBorder ? 1 : 0,
            BorderWidthTop = 1,
            BorderWidthRight = drawRightBorder ? 1 : 0,
            BorderWidthBottom = 1,
            ContentMarginLeft = 0,
            ContentMarginTop = 0,
            ContentMarginRight = 0,
            ContentMarginBottom = 0
        };
    }

    public static StyleBoxFlat CreateGutterPanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = GutterBackgroundColor,
            BorderColor = PanelBorderColor,
            BorderWidthLeft = 0,
            BorderWidthTop = 1,
            BorderWidthRight = 0,
            BorderWidthBottom = 1,
            ContentMarginLeft = 0,
            ContentMarginTop = 0,
            ContentMarginRight = 0,
            ContentMarginBottom = 0
        };
    }
}
