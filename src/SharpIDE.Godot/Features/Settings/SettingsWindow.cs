using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsWindow : Window
{
    private SpinBox _uiScaleSpinBox = null!;
    private LineEdit _debuggerFilePathLineEdit = null!;
    private CheckButton _debuggerUseSharpDbgCheckButton = null!;
    private OptionButton _themeOptionButton = null!;
    private Button _fontPicker = null!;
    private CheckButton _foldCode = null!;
    
    public override void _Ready()
    {
        CloseRequested += Hide;
        _uiScaleSpinBox = GetNode<SpinBox>("%UiScaleSpinBox");
        _debuggerFilePathLineEdit = GetNode<LineEdit>("%DebuggerFilePathLineEdit");
        _debuggerUseSharpDbgCheckButton = GetNode<CheckButton>("%DebuggerUseSharpDbgCheckButton");
        _themeOptionButton = GetNode<OptionButton>("%ThemeOptionButton");
        _fontPicker = GetNode<Button>("%FontPicker");
        _foldCode = GetNode<CheckButton>("%FoldCode");
        _uiScaleSpinBox.ValueChanged += OnUiScaleSpinBoxValueChanged;
        _debuggerFilePathLineEdit.TextChanged += OnDebuggerFilePathChanged;
        _debuggerUseSharpDbgCheckButton.Toggled += OnDebuggerUseSharpDbgToggled;
        _themeOptionButton.ItemSelected += OnThemeItemSelected;
        _fontPicker.Pressed += OnFontPickerPressed;
        _foldCode.Toggled += OnFoldCodeToggled;
        AboutToPopup += OnAboutToPopup;
    }

    private void OnAboutToPopup()
    {
        _uiScaleSpinBox.Value = Singletons.AppState.IdeSettings.UiScale;
        _debuggerFilePathLineEdit.Text = Singletons.AppState.IdeSettings.DebuggerExecutablePath;
        _debuggerUseSharpDbgCheckButton.ButtonPressed = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg;
        _fontPicker.Text = $"{Singletons.AppState.IdeSettings.EditorFont} | {Singletons.AppState.IdeSettings.FontSize}";
        if (!Singletons.AppState.IdeSettings.EditorFont.StartsWith("res://"))
        {
            var nfont = new SystemFont()
            {
                FontNames = [Singletons.AppState.IdeSettings.EditorFont]
            };
            _fontPicker.AddThemeFontOverride("font", nfont);
            _fontPicker.Text = $"{Singletons.AppState.IdeSettings.EditorFont} | {Singletons.AppState.IdeSettings.FontSize}";
        }
        else
        {
            _fontPicker.Text = $"Cascadia | {Singletons.AppState.IdeSettings.FontSize}";
        }

        _foldCode.ButtonPressed = Singletons.AppState.IdeSettings.AllowFolding;
        var themeOptionIndex = _themeOptionButton.GetOptionIndexOrNullForString(Singletons.AppState.IdeSettings.Theme.ToString());
        if (themeOptionIndex is not null) _themeOptionButton.Selected = themeOptionIndex.Value;
    }

    private void OnUiScaleSpinBoxValueChanged(double value)
    {
        var valueFloat = (float)value;
        Singletons.AppState.IdeSettings.UiScale = valueFloat;
        
        GetTree().GetRoot().ContentScaleFactor = valueFloat;
        PopupCenteredRatio(0.5f); // Re-size the window after scaling
    }

    private void OnDebuggerFilePathChanged(string newText)
    {
        Singletons.AppState.IdeSettings.DebuggerExecutablePath = newText;
    }
    
    private void OnDebuggerUseSharpDbgToggled(bool pressed)
    {
        Singletons.AppState.IdeSettings.DebuggerUseSharpDbg = pressed;
    }
    
    private void OnThemeItemSelected(long index)
    {
        var selectedTheme = _themeOptionButton.GetItemText((int)index);
        var lightOrDarkTheme = selectedTheme switch
        {
            "Light" => LightOrDarkTheme.Light,
            "Dark" => LightOrDarkTheme.Dark,
            _ => throw new InvalidOperationException($"Unknown theme selected: {selectedTheme}")
        };
        Singletons.AppState.IdeSettings.Theme = lightOrDarkTheme;
        this.SetIdeTheme(lightOrDarkTheme);
        GodotGlobalEvents.Instance.TextEditorThemeChanged.InvokeParallelFireAndForget(lightOrDarkTheme);
    }

    private void OnFontPickerPressed()
    {
        var dlg = GD.Load<PackedScene>("res://Features/Settings/FontPickerDialog.tscn").Instantiate<FontPickerDialog>();
        dlg.FontSelected += (font, size) =>
        {
            Singletons.AppState.IdeSettings.EditorFont = font;
            Singletons.AppState.IdeSettings.FontSize = size;
            Font nfont;
            if (font.StartsWith("res://"))
            {
                _fontPicker.Text = $"Cascadia | {size}";
                nfont = GD.Load<FontVariation>(font);
            }
            else
            {
                _fontPicker.Text = $"{font} | {size}";
                nfont = new SystemFont()
                {
                    FontNames = [font]
                };
            }
            _fontPicker.AddThemeFontOverride("font", nfont);
            GodotGlobalEvents.Instance.TextEditorFontChanged.InvokeParallelFireAndForget(nfont, size);
        };
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void OnFoldCodeToggled(bool value)
    {
        Singletons.AppState.IdeSettings.AllowFolding = value;
        GodotGlobalEvents.Instance.TextEditorCodeFoldingChanged.InvokeParallelFireAndForget(value);
    }
}