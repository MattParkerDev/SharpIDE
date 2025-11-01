using Godot;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class RenameSymbolDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;

    public TaskCompletionSource<string?> RenameTaskCompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string SymbolName { get; set; } = string.Empty;

    private bool _isNameValid = true;

    public override void _Ready()
    {
        _nameLineEdit = GetNode<LineEdit>("%SymbolNameLineEdit");
        _nameLineEdit.Text = SymbolName;
        _nameLineEdit.GrabFocus();
        _nameLineEdit.SelectAll();
        _nameLineEdit.TextChanged += ValidateNewSymbolName;
        Confirmed += OnConfirmed;
    }

    public override void _ExitTree()
    {
        RenameTaskCompletionSource.TrySetResult(null);
    }

    private void ValidateNewSymbolName(string newSymbolNameText)
    {
        _isNameValid = true;
        var newSymbolName = newSymbolNameText.Trim();
        if (string.IsNullOrEmpty(newSymbolName))
        {
            _isNameValid = false;
        }
        var textColour = _isNameValid ? new Color(1, 1, 1) : new Color(1, 0, 0);
        _nameLineEdit.AddThemeColorOverride(ThemeStringNames.FontColor, textColour);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter })
        {
            EmitSignalConfirmed();
        }
    }

    private void OnConfirmed()
    {
        if (_isNameValid is false) return;
        var newSymbolName = _nameLineEdit.Text.Trim();
        RenameTaskCompletionSource.SetResult(newSymbolName);
    }
}