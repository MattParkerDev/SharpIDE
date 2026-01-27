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
    private int _codeCompletionLineOfs = 0;
    private int _codeCompletionForceItemCenter = -1;
    private int _codeCompletionCurrentSelected = 0;
    private bool _isCodeCompletionScrollHovered = false;
    private bool _isCodeCompletionScrollPressed = false;
    private const int MaxLines = 7;

    private int? GetCompletionOptionAtPoint(Vector2I point)
    {
        if (!_codeCompletionRect.HasPoint(point)) return null;

        int rowHeight = GetLineHeight();
        int relativeY = point.Y - _codeCompletionRect.Position.Y;
        int lineIndex = relativeY / rowHeight + _codeCompletionLineOfs;
        if (lineIndex < 0 || lineIndex >= _codeCompletionOptions.Length) return null;

        return lineIndex;
    }

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

        var completionMaxWidth = 200;
        var codeCompletionLongestLine = Math.Min(completionMaxWidth,
            _codeCompletionOptions.MaxBy(s => s.CompletionItem.DisplayText.Length)!.CompletionItem.DisplayText.Length * fontSize);
        codeCompletionLongestLine = 500;

        _codeCompletionRect.Size = new Vector2I(
            codeCompletionLongestLine + codeCompletionIconSeparation + iconAreaSize.X + 2,
            completionsToDisplay * rowHeight
        );

        var caretPos = (Vector2I)GetCaretDrawPos();
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

		_codeCompletionRect.Position = new Vector2I(finalX, _codeCompletionRect.Position.Y);

		// var completionStyle = GetThemeStylebox(ThemeStringNames.Completion);
		// // I don't know what this is used for, but it puts a weird block box around the completions
		// completionStyle.Draw(
		// 	ci,
		// 	new Rect2(
		// 		_codeCompletionRect.Position - completionStyle.GetOffset(),
		// 		_codeCompletionRect.Size + codeCompletionMinimumSize + new Vector2I(scrollWidth, 0)
		// 	)
		// );

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

        _codeCompletionLineOfs = Mathf.Clamp(
            (_codeCompletionForceItemCenter < 0 ? _codeCompletionCurrentSelected : _codeCompletionForceItemCenter) -
            completionsToDisplay / 2,
            0,
            availableCompletions - completionsToDisplay
        );

        var codeCompletionSelectedColor = GetThemeColor(ThemeStringNames.CompletionSelectedColor);
        RenderingServer.Singleton.CanvasItemAddRect(
            ci,
            new Rect2(
                new Vector2(
                    _codeCompletionRect.Position.X,
                    _codeCompletionRect.Position.Y + (_codeCompletionCurrentSelected - _codeCompletionLineOfs) * rowHeight
                ),
                new Vector2(_codeCompletionRect.Size.X, rowHeight)
            ),
            codeCompletionSelectedColor
        );

        // TODO: Cache
        string lang = OS.GetLocale();
        for (int i = 0; i < completionsToDisplay; i++)
        {
            int l = _codeCompletionLineOfs + i;
            if (l < 0 || l >= availableCompletions)
            {
                GD.PushError($"Invalid line index: {l}");
                continue;
            }

            var sharpIdeCompletionItem = _codeCompletionOptions[l];
            var displayText = sharpIdeCompletionItem.CompletionItem.DisplayText;
            TextLine tl = new TextLine();
            tl.AddString(
                displayText,
                font,
                fontSize,
                lang
            );

            float yofs = (rowHeight - tl.GetSize().Y) / 2;
            Vector2 titlePos = new Vector2(
                _codeCompletionRect.Position.X,
                _codeCompletionRect.Position.Y + i * rowHeight + yofs
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

            tl.Width = _codeCompletionRect.Size.X - (iconAreaSize.X + codeCompletionIconSeparation);
            
            tl.Alignment = HorizontalAlignment.Left;
            

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

            var fontColour = Colors.White;
            tl.Draw(ci, titlePos, fontColour);
        }

        /* Draw a small scroll rectangle to show a position in the options. */
        if (scrollWidth > 0)
        {
            Color scrollColor = _isCodeCompletionScrollHovered || _isCodeCompletionScrollPressed
                ? GetThemeColor(ThemeStringNames.CompletionScrollHoveredColor)
                : GetThemeColor(ThemeStringNames.CompletionScrollColor);

            float r = (float)MaxLines / availableCompletions;
            float o = (float)_codeCompletionLineOfs / availableCompletions;

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
    }
}