using Godot;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private CompletionDescription? _selectedCompletionDescription;
    
    private void SetSelectedCompletion(int index)
    {
        _codeCompletionCurrentSelected = index;
        var currentSelectedCompletionItem = _codeCompletionOptions[_codeCompletionCurrentSelected].CompletionItem;
        _ = Task.GodotRun(async () =>
        {
            var description = await _roslynAnalysis.GetCompletionDescription(_currentFile, currentSelectedCompletionItem);
            _selectedCompletionDescription = description;
            await this.InvokeAsync(() =>
            {
                _completionDescriptionLabel.Clear();
                _completionDescriptionWindow.Size = new Vector2I(10, 10); // Used to shrink the window, as ChildControlsChanged() doesn't seem to handle shrinking in this case?
                CompletionDescriptionTooltip.WriteToCompletionDescriptionLabel(_completionDescriptionLabel, _selectedCompletionDescription, _syntaxHighlighter.ColourSetForTheme);
                if (_completionDescriptionWindow.Visible is false)
                {
                    _completionDescriptionWindow.Show();
                }
            });
        });
    }
    
    private bool CompletionsPopupTryConsumeGuiInput(InputEvent @event)
    {
        var isCodeCompletionPopupOpen = _codeCompletionOptions.Length is not 0;
        if (isCodeCompletionPopupOpen is false)
        {
            if (@event.IsActionPressed(InputStringNames.CodeEditorRequestCompletions))
            {
                _completionTrigger = new CompletionTrigger(CompletionTriggerKind.InvokeAndCommitIfUnique);
                CustomCodeCompletionRequested.InvokeParallelFireAndForget(_completionTrigger!.Value, Text, GetCaretPosition());
                return true;
            }
        }
        if (isCodeCompletionPopupOpen)
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } mouseEvent)
            {
                var codeCompletionIndex = GetCompletionOptionAtPoint((Vector2I)mouseEvent.Position);
                if (codeCompletionIndex is null)
                {
                    // if the index is null, it means we clicked outside the completion popup, so close it
                    ResetCompletionPopupState();
                    return false;
                }

                // If no item is currently centered
                if (_codeCompletionForceItemCenter is -1)
                {
                    // center the current central item, as an anchor, before we update the selection
                    _codeCompletionForceItemCenter = _codeCompletionCurrentSelected;
                }

                SetSelectedCompletion(codeCompletionIndex.Value);
                if (mouseEvent is { DoubleClick: true })
                {
                    ApplySelectedCodeCompletion();
                    return true;
                }

                GD.Print($"Code completion option clicked: {codeCompletionIndex.Value}");
                QueueRedraw();
                return true;
            }

            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown or MouseButton.WheelUp } scrollEvent)
            {
                if (_codeCompletionRect.HasPoint((Vector2I)scrollEvent.Position))
                {
                    int scrollAmount = scrollEvent.ButtonIndex is MouseButton.WheelDown ? 1 : -1;
                    var selectedIndex = Mathf.Clamp(
                        _codeCompletionCurrentSelected + scrollAmount,
                        0,
                        _codeCompletionOptions.Length - 1
                    );
                    _codeCompletionForceItemCenter = -1;
                    SetSelectedCompletion(selectedIndex);
                    QueueRedraw();
                    return true;
                }
            }
            else if (@event.IsActionPressed(InputStringNames.Backspace))
            {
                _pendingCompletionFilterReason = CompletionFilterReason.Deletion;
                return false;
            }

            if (@event is InputEventKey { Pressed: true, Keycode: Key.Up or Key.Down } inputEventKey)
            {
                var delta = inputEventKey.Keycode is Key.Up ? -1 : 1;
                var selectedIndex = Mathf.Clamp(
                    _codeCompletionCurrentSelected + delta,
                    0,
                    _codeCompletionOptions.Length - 1
                );
                _codeCompletionForceItemCenter = -1;
                SetSelectedCompletion(selectedIndex);
                QueueRedraw();
                return true;
            }

            if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                ResetCompletionPopupState();
                QueueRedraw();
                return true;
            }

            if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.Tab })
            {
                ApplySelectedCodeCompletion();
                return true;
            }
        }

        if (@event is InputEventKey { Pressed: true, Unicode: not 0 } keyEvent)
        {
            var unicodeString = char.ConvertFromUtf32((int)keyEvent.Unicode);
            if (isCodeCompletionPopupOpen)
            {
                if (unicodeString is " ")
                {
                    ResetCompletionPopupState();
                    QueueRedraw();
                    return false;
                }
                if (unicodeString is ".")
                {
                    ResetCompletionPopupState();
                    QueueRedraw();
                    _pendingCompletionTrigger = CompletionTrigger.CreateInsertionTrigger('.');
                    return false;
                }
                if (keyEvent.Unicode > 32)
                {
                    _pendingCompletionFilterReason = CompletionFilterReason.Insertion;
                    return false; // Let the text update happen
                }
            }

            if (isCodeCompletionPopupOpen is false && _codeCompletionTriggers.Contains(unicodeString, StringComparer.OrdinalIgnoreCase))
            {
                _pendingCompletionTrigger = CompletionTrigger.CreateInsertionTrigger(unicodeString[0]);
                return false;
            }
        }

        return false;
    }
}