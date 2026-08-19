using Godot;

namespace SharpIDE.Godot.Features.CodeEditor;

public sealed partial class CodeCompletionPopup
{
	private const int MaxLines = 7;
	private static readonly StyleBoxFlat SelectedCompletionStyle = new()
	{
		CornerRadiusTopLeft = 4,
		CornerRadiusTopRight = 4,
		CornerRadiusBottomLeft = 4,
		CornerRadiusBottomRight = 4,
	};

	private Rect2I _completionRect = new();
	private Rect2I _scrollRect = new();
	private readonly TextLine _completionTextLine = new();
	private readonly TextLine _inlineDescriptionTextLine = new();

	public void Draw()
	{
		if (IsOpen is false || _triggerPosition is null)
		{
			return;
		}

		const int iconSeparation = 4;
		const int scrollWidthWithOverflow = 6;
		const int iconOffset = 25;
		const int styleBoxOffset = 3;
		var font = _codeEdit.GetThemeFont(ThemeStringNames.Font);
		var fontSize = _codeEdit.GetThemeFontSize(ThemeStringNames.FontSize);
		var availableCompletions = _options.Length;
		var completionsToDisplay = Math.Min(availableCompletions, MaxLines);
		var rowHeight = _codeEdit.GetLineHeight();
		var iconAreaSize = new Vector2I(rowHeight, rowHeight);
		var lineOffsetEstimate = Mathf.Clamp(
			(_forceItemCenter < 0 ? _selectedIndex : _forceItemCenter) - completionsToDisplay / 2,
			0,
			availableCompletions - completionsToDisplay);
		var longestItem = _options
			.Skip(lineOffsetEstimate)
			.Take(completionsToDisplay)
			.MaxBy(item => item.CompletionItem.DisplayText.Length + item.CompletionItem.DisplayTextSuffix.Length + item.CompletionItem.InlineDescription.Length);
		var longestLine = (int)font.GetStringsSize(
			[longestItem.CompletionItem.GetEntireDisplayText(), " ", longestItem.CompletionItem.InlineDescription],
			HorizontalAlignment.Left,
			-1,
			fontSize).X + 10;
		longestLine = Math.Max(longestLine, _minLineWidth);
		_minLineWidth = longestLine;
		_completionRect.Size = new Vector2I(longestLine + iconSeparation + iconAreaSize.X + 2, completionsToDisplay * rowHeight);

		var caretPosition = _codeEdit.GetPosAtLineColumn(_codeEdit.GetCaretLine(), _codeEdit.GetCaretColumn());
		var totalHeight = 50 + _completionRect.Size.Y;
		var canFitAbove = caretPosition.Y - rowHeight > totalHeight;
		var canFitBelow = caretPosition.Y + rowHeight + totalHeight <= _codeEdit.Size.Y;
		var placeAbove = _preferAbove || canFitBelow is false && canFitAbove;
		if (_preferAbove is false && canFitBelow is false && canFitAbove is false)
		{
			var spaceAbove = caretPosition.Y - rowHeight;
			var spaceBelow = _codeEdit.Size.Y - caretPosition.Y;
			placeAbove = spaceAbove > spaceBelow;
			var availableSpace = (placeAbove ? spaceAbove : spaceBelow) - 50;
			completionsToDisplay = Mathf.Min(completionsToDisplay, Mathf.Max(1, (int)(availableSpace / rowHeight)));
			_completionRect.Size = new Vector2I(_completionRect.Size.X, completionsToDisplay * rowHeight);
			totalHeight = 50 + _completionRect.Size.Y;
		}

		var y = placeAbove
			? (int)(caretPosition.Y - totalHeight + rowHeight / 2.0f + 2)
			: (int)(caretPosition.Y + 1);
		var scrollWidth = availableCompletions > MaxLines ? scrollWidthWithOverflow : 0;
		var desiredX = _triggerPosition.Value.X - iconOffset;
		var maxX = (int)_codeEdit.Size.X - _completionRect.Size.X - scrollWidth;
		_completionRect.Position = new Vector2I(Math.Min(desiredX, maxX), y + styleBoxOffset);

		var completionStyle = _codeEdit.GetThemeStylebox(ThemeStringNames.Completion);
		completionStyle.Draw(
			_canvasItemRid,
			new Rect2(_completionRect.Position + new Vector2(-5, -5), _completionRect.Size + new Vector2(scrollWidth + 10, 10)));
		var backgroundColor = _codeEdit.GetThemeColor(ThemeStringNames.CompletionBackgroundColor);
		if (backgroundColor.A > 0.01f)
		{
			RenderingServer.Singleton.CanvasItemAddRect(
				_canvasItemRid,
				new Rect2(_completionRect.Position, _completionRect.Size + new Vector2I(scrollWidth, 0)),
				backgroundColor);
		}

		_scrollRect.Position = _completionRect.Position + new Vector2I(_completionRect.Size.X, 0);
		_scrollRect.Size = new Vector2I(scrollWidth, _completionRect.Size.Y);
		_lineOffset = Mathf.Clamp(
			(_forceItemCenter < 0 ? _selectedIndex : _forceItemCenter) - completionsToDisplay / 2,
			0,
			availableCompletions - completionsToDisplay);
		SelectedCompletionStyle.BgColor = _codeEdit.GetThemeColor(ThemeStringNames.CompletionSelectedColor);
		SelectedCompletionStyle.Draw(
			_canvasItemRid,
			new Rect2(
				new Vector2(_completionRect.Position.X, _completionRect.Position.Y + (_selectedIndex - _lineOffset) * rowHeight),
				new Vector2(_completionRect.Size.X, rowHeight)));

		var language = OS.GetLocale();
		for (var row = 0; row < completionsToDisplay; row++)
		{
			var optionIndex = _lineOffset + row;
			var completion = _options[optionIndex];
			var item = completion.CompletionItem;
			_completionTextLine.Clear();
			_completionTextLine.AddString(item.DisplayText, font, fontSize, language);
			_completionTextLine.AddString(item.DisplayTextSuffix, font, fontSize, language);
			_inlineDescriptionTextLine.Clear();
			_inlineDescriptionTextLine.AddString(" ", font, fontSize, language);
			_inlineDescriptionTextLine.AddString(item.InlineDescription, font, fontSize, language);
			var iconArea = new Rect2(
				new Vector2(_completionRect.Position.X, _completionRect.Position.Y + row * rowHeight),
				iconAreaSize);
			var icon = GetIcon(item);
			if (icon is not null)
			{
				var iconSize = iconArea.Size * 0.7f;
				icon.DrawRect(_canvasItemRid, new Rect2(iconArea.Position + (iconArea.Size - iconSize) / 2, iconSize), false);
			}

			var titlePosition = new Vector2(
				iconArea.End.X + iconSeparation,
				_completionRect.Position.Y + row * rowHeight + (rowHeight - _completionTextLine.GetSize().Y) / 2);
			_completionTextLine.Width = _completionRect.Size.X - iconAreaSize.X - iconSeparation;
			_completionTextLine.Alignment = HorizontalAlignment.Left;
			foreach (var matchSpan in completion.MatchedSpans ?? [])
			{
				var matchOffset = font.GetStringSize(item.DisplayText.Substr(0, matchSpan.Start), HorizontalAlignment.Left, -1, fontSize).X;
				var matchLength = font.GetStringSize(item.DisplayText.Substr(matchSpan.Start, matchSpan.Length), HorizontalAlignment.Left, -1, fontSize).X;
				RenderingServer.Singleton.CanvasItemAddRect(
					_canvasItemRid,
					new Rect2(new Vector2(titlePosition.X + matchOffset, _completionRect.Position.Y + row * rowHeight), new Vector2(matchLength, rowHeight)),
					_codeEdit.GetThemeColor(ThemeStringNames.CompletionExistingColor));
			}

			_completionTextLine.Draw(_canvasItemRid, titlePosition, EditorThemeColours.Dark.White);
			_inlineDescriptionTextLine.Draw(
				_canvasItemRid,
				new Vector2(titlePosition.X + _completionTextLine.GetSize().X, titlePosition.Y),
				EditorThemeColours.Dark.Gray);
		}

		if (scrollWidth > 0)
		{
			var scrollColor = _codeEdit.GetThemeColor(ThemeStringNames.CompletionScrollColor);
			var visibleRatio = (float)MaxLines / availableCompletions;
			var offsetRatio = (float)_lineOffset / availableCompletions;
			RenderingServer.Singleton.CanvasItemAddRect(
				_canvasItemRid,
				new Rect2(
					new Vector2(_completionRect.End.X, _completionRect.Position.Y + offsetRatio * _completionRect.Size.Y),
					new Vector2(scrollWidth, _completionRect.Size.Y * visibleRatio)),
				scrollColor);
		}

		_descriptionWindow.Position = new Vector2I(
			_completionRect.End.X + scrollWidth + 5,
			_completionRect.Position.Y - styleBoxOffset - 2) + (Vector2I)_codeEdit.GlobalPosition;
	}
}
