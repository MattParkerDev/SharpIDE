using Godot;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private bool CompletionsPopupTryConsumeGuiInput(InputEvent @event)
    {
        var isCodeCompletionPopupOpen = _codeCompletionOptions.Length is not 0;
        if (isCodeCompletionPopupOpen is false)
        {
            if (@event.IsActionPressed(InputStringNames.CodeEditorRequestCompletions))
            {
                completionTrigger = new CompletionTrigger(CompletionTriggerKind.InvokeAndCommitIfUnique);
                CustomCodeCompletionRequested.InvokeParallelFireAndForget(completionTrigger!.Value);
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

                _codeCompletionCurrentSelected = codeCompletionIndex.Value;
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
                    _codeCompletionCurrentSelected = Mathf.Clamp(
                        _codeCompletionCurrentSelected + scrollAmount,
                        0,
                        _codeCompletionOptions.Length - 1
                    );
                    _codeCompletionForceItemCenter = -1;
                    QueueRedraw();
                    return true;
                }
            }
            else if (@event.IsActionPressed(InputStringNames.Backspace))
            {
                pendingCompletionFilterReason = CompletionFilterReason.Deletion;
                return false;
            }

            if (@event is InputEventKey { Pressed: true, Keycode: Key.Up or Key.Down } inputEventKey)
            {
                var delta = inputEventKey.Keycode is Key.Up ? -1 : 1;
                _codeCompletionCurrentSelected = Mathf.Clamp(
                    _codeCompletionCurrentSelected + delta,
                    0,
                    _codeCompletionOptions.Length - 1
                );
                _codeCompletionForceItemCenter = -1;
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
            if (isCodeCompletionPopupOpen && keyEvent.Unicode >= 32)
            {
                pendingCompletionFilterReason = CompletionFilterReason.Insertion;
                return false; // Let the text update happen
            }

            if (isCodeCompletionPopupOpen is false && _codeCompletionTriggers.Contains(unicodeString, StringComparer.OrdinalIgnoreCase))
            {
                pendingCompletionTrigger = CompletionTrigger.CreateInsertionTrigger(unicodeString[0]);
                return false;
            }
        }

        return false;
    }
}