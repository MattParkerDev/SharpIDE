using Godot;

namespace SharpIDE.Godot.Features.Common;

/// <summary>
/// A <see cref="FoldableContainer"/> look-alike, styled from the theme's FoldableContainer items, whose title bar has a bold <see cref="Title"/> and, while folded,
/// a <see cref="Summary"/> filling the rest of the bar - a FoldableContainer's title bar controls only get their minimum width, at the far end.
/// Its children are the content, laid out like a PanelContainer's below the title bar, and hidden while folded.
/// </summary>
[Tool]
[GlobalClass]
public partial class CustomFoldableContainer : Container
{

	private readonly Button _titleBar = new() { ToggleMode = true, FocusMode = FocusModeEnum.None };
	private readonly HBoxContainer _titleBarContent = new() { MouseFilter = MouseFilterEnum.Ignore };
	private readonly TextureRect _arrow = new() { MouseFilter = MouseFilterEnum.Ignore, StretchMode = TextureRect.StretchModeEnum.KeepCentered };
	private readonly Label _titleLabel = new() { MouseFilter = MouseFilterEnum.Ignore };
	private readonly Label _summaryLabel = new()
	{
		MouseFilter = MouseFilterEnum.Ignore,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
		TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
		ThemeTypeVariation = ThemeVariationStringNames.Gray600Label
	};

	[Signal] public delegate void FoldingChangedEventHandler(bool folded);

	[Export]
	public string Title
	{
		get => _titleLabel.Text;
		set => _titleLabel.Text = value;
	}

	/// Shown beside the title while folded, e.g. the first line of the content
	[Export]
	public string Summary
	{
		get => _summaryLabel.Text;
		set => _summaryLabel.Text = value.ReplaceLineEndings(" ");
	}

	[Export]
	public bool Folded
	{
		get => _titleBar.ButtonPressed is false;
		set => _titleBar.ButtonPressed = value is false;
	}

	public CustomFoldableContainer()
	{
		_titleBarContent.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		_titleBarContent.AddThemeConstantOverride(ThemeStringNames.Separation, 6);
		_titleBarContent.AddChild(_arrow);
		_titleBarContent.AddChild(_titleLabel);
		_titleBarContent.AddChild(_summaryLabel);
		_titleBar.AddChild(_titleBarContent);
		// Internal, so it isn't saved with the scene, or counted among the content
		AddChild(_titleBar, @internal: InternalMode.Front);
		// Pressed is expanded
		_titleBar.Toggled += expanded =>
		{
			UpdateFolding();
			EmitSignalFoldingChanged(expanded is false);
		};
	}

	public override void _Notification(int what)
	{
		if (what == NotificationThemeChanged) ApplyTheme();
		else if (what == NotificationSortChildren) LayOutChildren();
		// Content added while folded is hidden too
		else if (what == NotificationChildOrderChanged) UpdateFolding();
	}

	private void ApplyTheme()
	{
		_titleLabel.AddThemeFontOverride(ThemeStringNames.Font, new FontVariation
		{
			BaseFont = GetThemeFont(ThemeStringNames.Font, GodotNodeStringNames.FoldableContainer),
			VariationOpentype = new global::Godot.Collections.Dictionary { [TextServerManager.GetPrimaryInterface().NameToTag("weight")] = 600 }
		});
		// The same whether folded or not (pressed is expanded) - the theme only styles the collapsed title bar, and Godot's default expanded one is near black
		var titlePanel = GetThemeStylebox(ThemeStringNames.FoldableContainer.TitleCollapsedPanel, GodotNodeStringNames.FoldableContainer);
		var titleHoverPanel = GetThemeStylebox(ThemeStringNames.FoldableContainer.TitleCollapsedHoverPanel, GodotNodeStringNames.FoldableContainer);
		_titleBar.AddThemeStyleboxOverride(ThemeStringNames.Normal, titlePanel);
		_titleBar.AddThemeStyleboxOverride(ThemeStringNames.Hover, titleHoverPanel);
		_titleBar.AddThemeStyleboxOverride(ThemeStringNames.Pressed, titlePanel);
		_titleBar.AddThemeStyleboxOverride(ThemeStringNames.HoverPressed, titleHoverPanel);
		// The bar's content isn't laid out by the button, so it takes the title panel's margins by hand
		_titleBarContent.OffsetLeft = Math.Max(titlePanel.GetMargin(Side.Left), 6);
		_titleBarContent.OffsetRight = -Math.Max(titlePanel.GetMargin(Side.Right), 6);
		UpdateFolding();
	}

	private void UpdateFolding()
	{
		var folded = Folded;
		_summaryLabel.Visible = folded;
		if (IsInsideTree())
		{
			_arrow.Texture = GetThemeIcon(folded ? ThemeStringNames.FoldableContainer.FoldedArrow : ThemeStringNames.FoldableContainer.ExpandedArrow, GodotNodeStringNames.FoldableContainer);
			_titleLabel.AddThemeColorOverride(ThemeStringNames.FontColor, GetThemeColor(folded ? ThemeStringNames.FoldableContainer.CollapsedFontColor : ThemeStringNames.FontColor, GodotNodeStringNames.FoldableContainer));
		}
		foreach (var child in ContentChildren) child.Visible = folded is false;
		UpdateMinimumSize();
		QueueSort();
		QueueRedraw();
	}

	private IEnumerable<Control> ContentChildren => GetChildren().OfType<Control>().Where(c => c.IsSetAsTopLevel() is false);

	private float TitleBarHeight()
	{
		// The bar's content isn't a container child of the button, so its height is added by hand
		var style = _titleBar.GetThemeStylebox(Folded ? ThemeStringNames.Normal : ThemeStringNames.Pressed);
		return Math.Max(_titleBarContent.GetCombinedMinimumSize().Y + style.GetMinimumSize().Y, 28);
	}

	public override Vector2 _GetMinimumSize()
	{
		var titleBar = _titleBarContent.GetCombinedMinimumSize() + _titleBar.GetThemeStylebox(ThemeStringNames.Normal).GetMinimumSize();
		var size = new Vector2(titleBar.X, TitleBarHeight());
		if (Folded) return size;
		var content = Vector2.Zero;
		foreach (var child in ContentChildren.Where(c => c.Visible))
		{
			content = content.Max(child.GetCombinedMinimumSize());
		}
		content += GetThemeStylebox(ThemeStringNames.Panel, GodotNodeStringNames.FoldableContainer).GetMinimumSize();
		return new Vector2(Math.Max(size.X, content.X), size.Y + content.Y);
	}

	private void LayOutChildren()
	{
		var titleBarHeight = TitleBarHeight();
		FitChildInRect(_titleBar, new Rect2(0, 0, Size.X, titleBarHeight));
		var panel = GetThemeStylebox(ThemeStringNames.Panel, GodotNodeStringNames.FoldableContainer);
		var contentRect = new Rect2(
			panel.GetMargin(Side.Left),
			titleBarHeight + panel.GetMargin(Side.Top),
			Size.X - panel.GetMargin(Side.Left) - panel.GetMargin(Side.Right),
			Size.Y - titleBarHeight - panel.GetMargin(Side.Top) - panel.GetMargin(Side.Bottom));
		foreach (var child in ContentChildren.Where(c => c.Visible))
		{
			FitChildInRect(child, contentRect);
		}
	}

	public override void _Draw()
	{
		if (Folded) return;
		var titleBarHeight = TitleBarHeight();
		DrawStyleBox(GetThemeStylebox(ThemeStringNames.Panel, GodotNodeStringNames.FoldableContainer), new Rect2(0, titleBarHeight, Size.X, Size.Y - titleBarHeight));
	}
}
