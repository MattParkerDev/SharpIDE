using Godot;
using System;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsWindow : Window
{
    private SpinBox _uiScaleSpinBox = null!;
    private LineEdit _debuggerFilePathLineEdit = null!;
    private CheckButton _debuggerUseSharpDbgCheckButton = null!;
    private OptionButton _themeOptionButton = null!;
    private LineEdit _backgroundImageLineEdit = null!;
    private Button _backgroundImageSelectBtn = null!;
    private HSlider _backgroundImageTransparencySlider = null!;
    private HSlider _codeBackgroundTransparencySlider = null!;
    private FileDialog _fileDialog = null!;
    private ColorPickerButton _currentLineHighlightColorPickerButton = null!;
    
    public override void _Ready()
    {
        CloseRequested += Hide;
        _uiScaleSpinBox = GetNode<SpinBox>("%UiScaleSpinBox");
        _debuggerFilePathLineEdit = GetNode<LineEdit>("%DebuggerFilePathLineEdit");
        _debuggerUseSharpDbgCheckButton = GetNode<CheckButton>("%DebuggerUseSharpDbgCheckButton");
        _themeOptionButton = GetNode<OptionButton>("%ThemeOptionButton");
        _backgroundImageLineEdit = GetNode<LineEdit>("%BackgroundImageLineEdit");
        _backgroundImageSelectBtn = GetNode<Button>("%BackgroundImageSelectBtn");
        _backgroundImageTransparencySlider = GetNode<HSlider>("%BackgroundImageTransparencySlider");
        _codeBackgroundTransparencySlider = GetNode<HSlider>("%CodeBackgroundTransparencySlider");
        _currentLineHighlightColorPickerButton = GetNode<ColorPickerButton>("%CurrentLineHighlightColorPickerButton");
        _currentLineHighlightColorPickerButton.EditAlpha = true;
        _fileDialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures),
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = ["*.png, *.jpg, *.jpeg, *.svg, *.bmp, *.webp ; Image Files"]
        };
        _uiScaleSpinBox.ValueChanged += OnUiScaleSpinBoxValueChanged;
        _debuggerFilePathLineEdit.TextChanged += OnDebuggerFilePathChanged;
        _debuggerUseSharpDbgCheckButton.Toggled += OnDebuggerUseSharpDbgToggled;
        _themeOptionButton.ItemSelected += OnThemeItemSelected;
        _backgroundImageSelectBtn.Pressed += OnBackgroundImageSelectBtnPressed;
        _backgroundImageTransparencySlider.ValueChanged += OnBackgroundImageTransparencySliderChanged;
        _codeBackgroundTransparencySlider.ValueChanged += OnCodeBackgroundTransparencySliderChanged;
        _currentLineHighlightColorPickerButton.ColorChanged += OnCurrentLineHighlightColorChanged;
        _fileDialog.FileSelected += OnFileDialogFileSelected;
        AboutToPopup += OnAboutToPopup;
        AddChild(_fileDialog);
    }

    private void OnAboutToPopup()
    {
        _uiScaleSpinBox.Value = Singletons.AppState.IdeSettings.UiScale;
        _debuggerFilePathLineEdit.Text = Singletons.AppState.IdeSettings.DebuggerExecutablePath;
        _debuggerUseSharpDbgCheckButton.ButtonPressed = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg;
        var themeOptionIndex = _themeOptionButton.GetOptionIndexOrNullForString(Singletons.AppState.IdeSettings.Theme.ToString());
        if (themeOptionIndex is not null) _themeOptionButton.Selected = themeOptionIndex.Value;
        _backgroundImageLineEdit.Text = Singletons.AppState.IdeSettings.BackgroundImagePath;
        _backgroundImageTransparencySlider.Value = Singletons.AppState.IdeSettings.BackgroundImageTransparency;
        _codeBackgroundTransparencySlider.Value = Singletons.AppState.IdeSettings.CodeBackgroundTransparency;
        _currentLineHighlightColorPickerButton.Color = new Color(Singletons.AppState.IdeSettings.CurrentLineHighlightColor);
    }

    private void OnUiScaleSpinBoxValueChanged(double value)
    {
        var valueFloat = (float)value;
        Singletons.AppState.IdeSettings.UiScale = valueFloat;
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        
        GetTree().GetRoot().ContentScaleFactor = valueFloat;
        PopupCenteredRatio(0.5f); // Re-size the window after scaling
    }

    private void OnDebuggerFilePathChanged(string newText)
    {
        Singletons.AppState.IdeSettings.DebuggerExecutablePath = newText;
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
    }
    
    private void OnDebuggerUseSharpDbgToggled(bool pressed)
    {
        Singletons.AppState.IdeSettings.DebuggerUseSharpDbg = pressed;
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
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
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        this.SetIdeTheme(lightOrDarkTheme);
        GodotGlobalEvents.Instance.TextEditorThemeChanged.InvokeParallelFireAndForget(lightOrDarkTheme);
    }

    private void OnBackgroundImageSelectBtnPressed()
    {
        _fileDialog.PopupCentered();
    }

    private void OnFileDialogFileSelected(string path)
    {
        _backgroundImageLineEdit.Text = path;
        Singletons.AppState.IdeSettings.BackgroundImagePath = path;
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        GodotGlobalEvents.Instance.BackgroundImageChanged.InvokeParallelFireAndForget(path);
    }

    private void OnBackgroundImageTransparencySliderChanged(double newValue)
    {
        Singletons.AppState.IdeSettings.BackgroundImageTransparency = newValue;
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        GodotGlobalEvents.Instance.BackgroundTransparencyChanged.InvokeParallelFireAndForget(newValue);
    }

    private void OnCodeBackgroundTransparencySliderChanged(double newValue)
    {
        Singletons.AppState.IdeSettings.CodeBackgroundTransparency = newValue;
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        GodotGlobalEvents.Instance.CodeBackgroundTransparencyChanged.InvokeParallelFireAndForget(newValue);
    }

    private void OnCurrentLineHighlightColorChanged(Color color)
    {
        Singletons.AppState.IdeSettings.CurrentLineHighlightColor = color.ToHtml();
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        GodotGlobalEvents.Instance.CurrentLineHighlightColorChanged.InvokeParallelFireAndForget(color);
    }
}
