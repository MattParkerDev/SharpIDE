using System.Collections.Immutable;
using Godot;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private ImmutableArray<SharpIdeCompletionItem> _codeCompletionOptions = [];
    
    private Rect2I _codeCompletionRect = new Rect2I();
    private Rect2I _codeCompletionScrollRect = new Rect2I();
    private Vector2I _codeHintMinsize = new Vector2I();
    private Vector2I? _completionTriggerPosition;
    private int _codeCompletionLineOffset = 0;
    private int _codeCompletionForceItemCenter = -1;
    private int _codeCompletionCurrentSelected = 0;
    private int _codeCompletionMinLineWidth = 0;
    private bool _isCodeCompletionScrollHovered = false;
    private bool _isCodeCompletionScrollPressed = false;
    private const int MaxLines = 7;
    
    private static readonly StyleBoxFlat SelectedCompletionStyle = new StyleBoxFlat
    {
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4
    };

    private int? GetCompletionOptionAtPoint(Vector2I point)
    {
        if (!_codeCompletionRect.HasPoint(point)) return null;

        int rowHeight = GetLineHeight();
        int relativeY = point.Y - _codeCompletionRect.Position.Y;
        int lineIndex = relativeY / rowHeight + _codeCompletionLineOffset;
        if (lineIndex < 0 || lineIndex >= _codeCompletionOptions.Length) return null;

        return lineIndex;
    }

    private TextLine _completionTextLine = new TextLine();
    private TextLine _completionInlineDescriptionTextLine = new TextLine();
    private void DrawCompletionsPopup()
    {
        var drawCodeCompletion = _codeCompletionOptions.Length > 0;
        var drawCodeHint = false;
        var codeHintDrawBelow = false;

        if (!drawCodeCompletion) return;

        // originally from theme cache
        const int codeCompletionIconSeparation = 4;
        var codeCompletionMinimumSize = new Vector2I(50, 50);
        var lineSpacing = 2;
        var themeScrollWidth = 6;
        //

        var font = GetThemeFont(ThemeStringNames.Font);
        var fontSize = GetThemeFontSize(ThemeStringNames.FontSize);
        var ci = _aboveCanvasItemRid!.Value;
        var availableCompletions = _codeCompletionOptions.Length;
        var completionsToDisplay = Math.Min(availableCompletions, MaxLines);
        var rowHeight = GetLineHeight();
        var iconAreaSize = new Vector2I(rowHeight, rowHeight);

        var lineOffsetEstimate = Mathf.Clamp(
            (_codeCompletionForceItemCenter < 0 ? _codeCompletionCurrentSelected : _codeCompletionForceItemCenter) - completionsToDisplay / 2,
            0, availableCompletions - completionsToDisplay
        );
        var longestCompletionItem = _codeCompletionOptions
            .Skip(lineOffsetEstimate)
            .Take(completionsToDisplay)
            .MaxBy(s => s.CompletionItem.DisplayText.Length + s.CompletionItem.DisplayTextSuffix.Length + s.CompletionItem.InlineDescription.Length);

        var codeCompletionLongestLine = (int)font.GetStringsSize([longestCompletionItem.CompletionItem.GetEntireDisplayText(), " ", longestCompletionItem.CompletionItem.InlineDescription], HorizontalAlignment.Left, -1, fontSize).X + 10; // add some padding to prevent clipping
        if (codeCompletionLongestLine < _codeCompletionMinLineWidth)
        {
            codeCompletionLongestLine = _codeCompletionMinLineWidth;
        }
        _codeCompletionMinLineWidth = codeCompletionLongestLine;

        _codeCompletionRect.Size = new Vector2I(
            codeCompletionLongestLine + codeCompletionIconSeparation + iconAreaSize.X + 2,
            completionsToDisplay * rowHeight
        );

        var caretLinePos = GetCaretPosition();
        var caretPos = GetPosAtLineColumn(caretLinePos.line, caretLinePos.col);
        var totalHeight = codeCompletionMinimumSize.Y + _codeCompletionRect.Size.Y;
        float minY = caretPos.Y - rowHeight;
        float maxY = caretPos.Y + rowHeight + totalHeight;

        // if (drawCodeHint)
        // {
        //     if (codeHintDrawBelow)
        //     {
        //         maxY += codeHintMinsize.Y;
        //     }
        //     else
        //     {
        //         minY -= codeHintMinsize. Y;
        //     }
        // }

        bool canFitCompletionAbove = minY > totalHeight;
        var sharpIdeCodeEditSize = GetSize();
        bool canFitCompletionBelow = maxY <= sharpIdeCodeEditSize.Y;

        bool shouldPlaceAbove = !canFitCompletionBelow && canFitCompletionAbove;

        if (!canFitCompletionBelow && !canFitCompletionAbove)
        {
            float spaceAbove = caretPos.Y - rowHeight;
            float spaceBelow = sharpIdeCodeEditSize.Y - caretPos.Y;
            shouldPlaceAbove = spaceAbove > spaceBelow;

            // Reduce the line count and recalculate heights to better fit the completion popup. 
            float spaceAvail;
            if (shouldPlaceAbove)
            {
                spaceAvail = spaceAbove - codeCompletionMinimumSize.Y;
            }
            else
            {
                spaceAvail = spaceBelow - codeCompletionMinimumSize.Y;
            }

            int maxLinesFit = Mathf.Max(1, (int)(spaceAvail / rowHeight));
            completionsToDisplay = Mathf.Min(completionsToDisplay, maxLinesFit);
            _codeCompletionRect.Size = new Vector2I(_codeCompletionRect.Size.X, completionsToDisplay * rowHeight);
            totalHeight = codeCompletionMinimumSize.Y + _codeCompletionRect.Size.Y;
        }

        if (shouldPlaceAbove)
        {
            _codeCompletionRect.Position = new Vector2I(
                _codeCompletionRect.Position.X,
                (caretPos.Y - totalHeight - rowHeight) + lineSpacing
            );
            if (drawCodeHint && !codeHintDrawBelow)
            {
                _codeCompletionRect.Position = new Vector2I(
                    _codeCompletionRect.Position.X,
                    _codeCompletionRect.Position.Y - _codeHintMinsize.Y
                );
            }
        }
        else
        {
            _codeCompletionRect.Position = new Vector2I(
                _codeCompletionRect.Position.X,
                caretPos.Y + (lineSpacing / 2)
            );
            if (drawCodeHint && codeHintDrawBelow)
            {
                _codeCompletionRect.Position = new Vector2I(
                    _codeCompletionRect.Position.X,
                    _codeCompletionRect.Position.Y + _codeHintMinsize.Y
                );
            }
        }

        var scrollWidth = availableCompletions > MaxLines ? themeScrollWidth : 0;

        // TODO: Fix
        var codeCompletionBase = "";
        
        const int iconOffset = 25;
		// Desired X position for the popup to start at
		int desiredX = _completionTriggerPosition!.Value.X - iconOffset;

		// Calculate the maximum X allowed so the popup stays inside the parent
		int maxX = (int)sharpIdeCodeEditSize.X - _codeCompletionRect.Size.X - scrollWidth;

		// Clamp the X position so it never overflows to the right
		int finalX = Math.Min(desiredX, maxX);

        const int styleBoxOffset = 3;
		_codeCompletionRect.Position = new Vector2I(finalX, _codeCompletionRect.Position.Y + styleBoxOffset);

		var completionStyle = GetThemeStylebox(ThemeStringNames.Completion);
		completionStyle.Draw(
			ci,
			new Rect2(
				_codeCompletionRect.Position + new Vector2(-5, -5),
				_codeCompletionRect.Size + new Vector2(scrollWidth, 0) + new Vector2(10, 10)
			)
		);

        var codeCompletionBackgroundColor = GetThemeColor(ThemeStringNames.CompletionBackgroundColor);
        if (codeCompletionBackgroundColor.A > 0.01f)
        {
            RenderingServer.Singleton.CanvasItemAddRect(
                ci,
                new Rect2(_codeCompletionRect.Position, _codeCompletionRect.Size + new Vector2I(scrollWidth, 0)),
                codeCompletionBackgroundColor
            );
        }

        _codeCompletionScrollRect.Position = _codeCompletionRect.Position + new Vector2I(_codeCompletionRect.Size.X, 0);
        _codeCompletionScrollRect.Size = new Vector2I(scrollWidth, _codeCompletionRect.Size.Y);

        _codeCompletionLineOffset = Mathf.Clamp(
            (_codeCompletionForceItemCenter < 0 ? _codeCompletionCurrentSelected : _codeCompletionForceItemCenter) -
            completionsToDisplay / 2,
            0,
            availableCompletions - completionsToDisplay
        );

        var codeCompletionSelectedColor = GetThemeColor(ThemeStringNames.CompletionSelectedColor);
        SelectedCompletionStyle.BgColor = codeCompletionSelectedColor;
        SelectedCompletionStyle.Draw(
            ci,
            new Rect2(
                new Vector2(
                    _codeCompletionRect.Position.X,
                    _codeCompletionRect.Position.Y + (_codeCompletionCurrentSelected - _codeCompletionLineOffset) * rowHeight
                ),
                new Vector2(_codeCompletionRect.Size.X, rowHeight)
            )
        );

        // TODO: Cache
        string lang = OS.GetLocale();
        for (int i = 0; i < completionsToDisplay; i++)
        {
            int l = _codeCompletionLineOffset + i;
            if (l < 0 || l >= availableCompletions)
            {
                GD.PushError($"Invalid line index: {l}");
                continue;
            }

            var sharpIdeCompletionItem = _codeCompletionOptions[l];
            var displayText = sharpIdeCompletionItem.CompletionItem.DisplayText;
            
            var textLine = _completionTextLine;
            textLine.Clear();
            textLine.AddString(displayText, font, fontSize, lang);
            textLine.AddString(sharpIdeCompletionItem.CompletionItem.DisplayTextSuffix, font, fontSize, lang);
            _completionInlineDescriptionTextLine.Clear();
            _completionInlineDescriptionTextLine.AddString(" ", font, fontSize, lang);
            _completionInlineDescriptionTextLine.AddString(sharpIdeCompletionItem.CompletionItem.InlineDescription, font, fontSize, lang);

            float yOffset = (rowHeight - textLine.GetSize().Y) / 2;
            Vector2 titlePos = new Vector2(
                _codeCompletionRect.Position.X,
                _codeCompletionRect.Position.Y + i * rowHeight + yOffset
            );

            /* Draw completion icon if it is valid. */
            var icon = GetIconForCompletion(sharpIdeCompletionItem);
            Rect2 iconArea = new Rect2(
                new Vector2(_codeCompletionRect.Position.X, _codeCompletionRect.Position.Y + i * rowHeight),
                iconAreaSize
            );

            if (icon != null)
            {
                Vector2 iconSize = iconArea.Size * 0.7f;
                icon.DrawRect(
                    ci,
                    new Rect2(
                        iconArea.Position + (iconArea.Size - iconSize) / 2,
                        iconSize
                    ),
                    false
                );
            }

            titlePos.X = iconArea.Position.X + iconArea.Size.X + codeCompletionIconSeparation;

            textLine.Width = _codeCompletionRect.Size.X - (iconAreaSize.X + codeCompletionIconSeparation);
            
            textLine.Alignment = HorizontalAlignment.Left;
            

            Vector2 matchPos = new Vector2(
                _codeCompletionRect.Position.X + iconAreaSize.X + codeCompletionIconSeparation,
                _codeCompletionRect.Position.Y + i * rowHeight
            );

            foreach (var matchSegment in sharpIdeCompletionItem.MatchedSpans ?? [])
            {
                float matchOffset = font.GetStringSize(
                    displayText.Substr(0, matchSegment.Start),
                    HorizontalAlignment.Left,
                    -1,
                    fontSize
                ).X;

                float matchLen = font.GetStringSize(
                    displayText.Substr(matchSegment.Start, matchSegment.Length),
                    HorizontalAlignment.Left,
                    -1,
                    fontSize
                ).X;

                RenderingServer.Singleton.CanvasItemAddRect(
                    ci,
                    new Rect2(matchPos + new Vector2(matchOffset, 0), new Vector2(matchLen, rowHeight)),
                    GetThemeColor(ThemeStringNames.CompletionExistingColor)
                );
            }

            var fontColour = EditorThemeColours.Dark.White;
            textLine.Draw(ci, titlePos, fontColour);
            var inlineDescriptionPos = new Vector2(titlePos.X + textLine.GetSize().X, titlePos.Y);
            _completionInlineDescriptionTextLine.Draw(ci, inlineDescriptionPos, EditorThemeColours.Dark.Gray);
        }

        /* Draw a small scroll rectangle to show a position in the options. */
        if (scrollWidth > 0)
        {
            Color scrollColor = _isCodeCompletionScrollHovered || _isCodeCompletionScrollPressed
                ? GetThemeColor(ThemeStringNames.CompletionScrollHoveredColor)
                : GetThemeColor(ThemeStringNames.CompletionScrollColor);

            float r = (float)MaxLines / availableCompletions;
            float o = (float)_codeCompletionLineOffset / availableCompletions;

            RenderingServer.Singleton.CanvasItemAddRect(
                ci,
                new Rect2(
                    new Vector2(
                        _codeCompletionRect.Position.X + _codeCompletionRect.Size.X,
                        _codeCompletionRect.Position.Y + o * _codeCompletionRect.Size.Y
                    ),
                    new Vector2(scrollWidth, _codeCompletionRect.Size.Y * r)
                ),
                scrollColor
            );
        }
        
        const int completionDescriptionStyleBoxOffset = 2;
        var descriptionPos = new Vector2I(
            _codeCompletionRect.Position.X + _codeCompletionRect.Size.X + scrollWidth + 5,
            _codeCompletionRect.Position.Y - styleBoxOffset - completionDescriptionStyleBoxOffset
        );
        _completionDescriptionWindow.Position = descriptionPos + (Vector2I)GlobalPosition;
    }
}
