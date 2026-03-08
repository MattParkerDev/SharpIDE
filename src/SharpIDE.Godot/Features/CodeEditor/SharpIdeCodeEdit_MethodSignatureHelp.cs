using Godot;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private bool _isMethodSignatureHelpPopupOpen;
    private LinePositionSpan _signatureHelpApplicableSpan;
    private LinePosition? _previousCaretPositionForMethodSignatureHelp;

    private void CloseMethodSignatureHelpWindow()
    {
        _isMethodSignatureHelpPopupOpen = false;
        _methodSignatureHelpWindow.Hide();
    }
    
    private bool MethodSignatureHelpPopupTryConsumeGuiInput(InputEvent @event)
    {
        if (_isMethodSignatureHelpPopupOpen)
        {
            var (caretLine, caretCol) = GetCaretPosition();
            var caretPositionLinePosition = new LinePosition(caretLine, caretCol);
            if (caretPositionLinePosition < _signatureHelpApplicableSpan.Start || caretPositionLinePosition > _signatureHelpApplicableSpan.End)
            {
                CloseMethodSignatureHelpWindow();
                return false;
            }

            if (_previousCaretPositionForMethodSignatureHelp != caretPositionLinePosition)
            {
                UpdateSignatureHelpTooltip(caretLine, caretCol, false);
                return false;
            }
        }
        if (@event is InputEventMouseButton) return false;
        if (@event.IsActionPressed(InputStringNames.Cancel) && _isMethodSignatureHelpPopupOpen)
        {
            CloseMethodSignatureHelpWindow();
            return true;
        }
        else if (@event.IsActionPressed(InputStringNames.CodeEditorRequestSignatureInfo))
        {
            var (caretLine, caretColumn) = GetCaretPosition();
            UpdateSignatureHelpTooltip(caretLine, caretColumn, true); 
            return true;
        }
        return false;
    }

    private void UpdateSignatureHelpTooltip(int caretLine, int caretColumn, bool showIfHidden)
    {
        var caretPos = GetPosAtLineColumn(caretLine, caretColumn);
        _ = Task.GodotRun(async () =>
        {
            var linePos = new LinePosition(caretLine, caretColumn);
            var sharpIdeSignatureHelpItems = await _roslynAnalysis.GetMethodSignatureInfo(_currentFile, linePos);
            if (sharpIdeSignatureHelpItems is null) return;
            var signatureHelpItems = sharpIdeSignatureHelpItems.Items;
            _signatureHelpApplicableSpan = sharpIdeSignatureHelpItems.ApplicableSpan;
            await this.InvokeAsync(() =>
            {
                var richTextLabel = _methodSignatureHelpWindow.GetNode<RichTextLabel>("PanelContainer/RichTextLabel");
                richTextLabel.Clear();
                _methodSignatureHelpWindow.Size = new Vector2I(10, 10); // Used to shrink the window, as ChildControlsChanged() doesn't seem to handle shrinking in this case?
                MethodSignatureHelpTooltip.WriteToMethodSignatureHelpLabel(richTextLabel, signatureHelpItems, _syntaxHighlighter.ColourSetForTheme);
                SetSignatureHelpTooltipPosition(caretPos);
                _previousCaretPositionForMethodSignatureHelp = linePos;
                if (showIfHidden)
                {
                    _methodSignatureHelpWindow.Show(); 
                    _isMethodSignatureHelpPopupOpen = true;
                }
            });
        });
    }
    
    private void SetSignatureHelpTooltipPosition(Vector2I caretPos)
    {
        _methodSignatureHelpWindow.Position = (Vector2I)GetGlobalPosition() + caretPos;
    }

    private void OnCodeEditScrolled(double delta)
    {
        if (_isMethodSignatureHelpPopupOpen)
        {
            var (caretLine, caretCol) = GetCaretPosition();
            // Let the CodeEdit actually apply the scroll first
            Callable.From(() =>
            {
                var caretPos = GetPosAtLineColumn(caretLine, caretCol);
                SetSignatureHelpTooltipPosition(caretPos);
            }).CallDeferred();
        }
    }
}