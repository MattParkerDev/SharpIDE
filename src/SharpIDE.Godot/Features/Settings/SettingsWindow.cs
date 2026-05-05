using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsWindow : Window
{
    private SpinBox _uiScaleSpinBox = null!;
    private LineEdit _debuggerFilePathLineEdit = null!;
    private CheckButton _debuggerUseSharpDbgCheckButton = null!;
    private OptionButton _themeOptionButton = null!;
    private Button _fontPickerButton = null!;
    private Button _terminalFontPickerButton = null!;
    private CheckButton _foldCodeCheckButton = null!;

    private PackedScene _fontPickerDialogScene = ResourceLoader.Load<PackedScene>("uid://bkw3m18ndkev3");

    public override void _Ready()
    {
        CloseRequested += Hide;
        _uiScaleSpinBox = GetNode<SpinBox>("%UiScaleSpinBox");
        _debuggerFilePathLineEdit = GetNode<LineEdit>("%DebuggerFilePathLineEdit");
        _debuggerUseSharpDbgCheckButton = GetNode<CheckButton>("%DebuggerUseSharpDbgCheckButton");
        _themeOptionButton = GetNode<OptionButton>("%ThemeOptionButton");
        _fontPickerButton = GetNode<Button>("%FontPickerButton");
        _terminalFontPickerButton = GetNode<Button>("%TerminalFontPickerButton");
        _foldCodeCheckButton = GetNode<CheckButton>("%FoldCodeCheckButton");
        
        _uiScaleSpinBox.ValueChanged += OnUiScaleSpinBoxValueChanged;
        _debuggerFilePathLineEdit.TextChanged += OnDebuggerFilePathChanged;
        _debuggerUseSharpDbgCheckButton.Toggled += OnDebuggerUseSharpDbgToggled;
        _themeOptionButton.ItemSelected += OnThemeItemSelected;
        _fontPickerButton.Pressed += OnFontPickerButtonPressed;
        _terminalFontPickerButton.Pressed += OnTerminalFontPickerButtonPressed;
        _foldCodeCheckButton.Toggled += OnFoldCodeCheckButtonToggled;
        AboutToPopup += OnAboutToPopup;
    }

    private void OnAboutToPopup()
    {
        _uiScaleSpinBox.Value = Singletons.AppState.IdeSettings.UiScale;
        _debuggerFilePathLineEdit.Text = Singletons.AppState.IdeSettings.DebuggerExecutablePath;
        _debuggerUseSharpDbgCheckButton.ButtonPressed = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg;
        var currentCodeEditThemeFont = GetThemeFont(ThemeStringNames.Font, GodotNodeStringNames.CodeEdit);
        var currentCodeEditThemeFontSize = GetThemeFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.CodeEdit);
        _fontPickerButton.AddThemeFontOverride(ThemeStringNames.Font, currentCodeEditThemeFont);
        _fontPickerButton.Text = $"{currentCodeEditThemeFont.GetFontName()} | {currentCodeEditThemeFontSize}";
        var currentTerminalThemeNormalFont = GetThemeFont(ThemeStringNames.Terminal.NormalFont, GodotNodeStringNames.Terminal);
        _terminalFontPickerButton.AddThemeFontOverride(ThemeStringNames.Font, currentTerminalThemeNormalFont);
        _terminalFontPickerButton.Text = $"{currentTerminalThemeNormalFont.GetFontName()} | {GetThemeFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.Terminal)}";

        _foldCodeCheckButton.ButtonPressed = Singletons.AppState.IdeSettings.EditorEnableFolding;
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

    private void OnFontPickerButtonPressed()
    {
        var fontPickerDialog = _fontPickerDialogScene.Instantiate<FontPickerDialog>();
        fontPickerDialog.CurrentSystemFontName = Singletons.AppState.IdeSettings.EditorSystemFontName;
        fontPickerDialog.CurrentFontSize = Singletons.AppState.IdeSettings.EditorFontSize;
        fontPickerDialog.DefaultFont = SetThemeExtensions.EditorDefaultFont;
        fontPickerDialog.DefaultFontSize = SetThemeExtensions.EditorDefaultFontSize;
        fontPickerDialog.FontSelected += result =>
        {
            var (systemFontName, selectedFontSize) = result;
            Singletons.AppState.IdeSettings.EditorSystemFontName = systemFontName;
            Singletons.AppState.IdeSettings.EditorFontSize = selectedFontSize;
            var font = systemFontName is null ? SetThemeExtensions.EditorDefaultFont : new SystemFont { FontNames = [systemFontName] };
            var fontSize = selectedFontSize ?? SetThemeExtensions.EditorDefaultFontSize;
            _fontPickerButton.Text = $"{font.GetFontName()} | {fontSize}";
            _fontPickerButton.AddThemeFontOverride(ThemeStringNames.Font, font);
            this.ThemeSetCodeEditFont(font);
            this.ThemeSetCodeEditFontSize(fontSize);
        };
        AddChild(fontPickerDialog);
        fontPickerDialog.PopupCentered();
    }
    
    private void OnTerminalFontPickerButtonPressed()
    {
        var fontPickerDialog = _fontPickerDialogScene.Instantiate<FontPickerDialog>();
        fontPickerDialog.CurrentSystemFontName = Singletons.AppState.IdeSettings.TerminalSystemFontName;
        fontPickerDialog.CurrentFontSize = Singletons.AppState.IdeSettings.TerminalFontSize;
        fontPickerDialog.DefaultFont = SetThemeExtensions.TerminalDefaultFont;
        fontPickerDialog.DefaultFontSize = SetThemeExtensions.TerminalDefaultFontSize;
        fontPickerDialog.FontSelected += result =>
        {
            var (systemFontName, selectedFontSize) = result;
            Singletons.AppState.IdeSettings.TerminalSystemFontName = systemFontName;
            Singletons.AppState.IdeSettings.TerminalFontSize = selectedFontSize;
            var font = systemFontName is null ? SetThemeExtensions.TerminalDefaultFont : new SystemFont { FontNames = [systemFontName] };
            var fontSize = selectedFontSize ?? SetThemeExtensions.TerminalDefaultFontSize;
            _terminalFontPickerButton.Text = $"{font.GetFontName()} | {fontSize}";
            _terminalFontPickerButton.AddThemeFontOverride(ThemeStringNames.Font, font);
            this.ThemeSetTerminalFont(font);
            this.ThemeSetTerminalFontSize(fontSize);
        };
        AddChild(fontPickerDialog);
        fontPickerDialog.PopupCentered();
    }

    private void OnFoldCodeCheckButtonToggled(bool value)
    {
        Singletons.AppState.IdeSettings.EditorEnableFolding = value;
        GodotGlobalEvents.Instance.TextEditorCodeFoldingChanged.InvokeParallelFireAndForget(value);
    }
}