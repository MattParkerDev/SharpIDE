using Godot;
using SharpIDE.Godot.Features.ExtensionManager;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsWindow : Window
{
    private SpinBox _uiScaleSpinBox = null!;
    private LineEdit _debuggerFilePathLineEdit = null!;
    private CheckButton _debuggerUseSharpDbgCheckButton = null!;
    private OptionButton _themeOptionButton = null!;
    private ExtensionManagerWindow? _extensionManagerWindow;
    
    public override void _Ready()
    {
        CloseRequested += Hide;
        _uiScaleSpinBox = GetNode<SpinBox>("%UiScaleSpinBox");
        _debuggerFilePathLineEdit = GetNode<LineEdit>("%DebuggerFilePathLineEdit");
        _debuggerUseSharpDbgCheckButton = GetNode<CheckButton>("%DebuggerUseSharpDbgCheckButton");
        _themeOptionButton = GetNode<OptionButton>("%ThemeOptionButton");
        _uiScaleSpinBox.ValueChanged += OnUiScaleSpinBoxValueChanged;
        _debuggerFilePathLineEdit.TextChanged += OnDebuggerFilePathChanged;
        _debuggerUseSharpDbgCheckButton.Toggled += OnDebuggerUseSharpDbgToggled;
        _themeOptionButton.ItemSelected += OnThemeItemSelected;
        AboutToPopup += OnAboutToPopup;
        AddLanguageExtensionsControls();
    }

    private void AddLanguageExtensionsControls()
    {
        var settingsVBox = _uiScaleSpinBox.GetParent()?.GetParent()?.GetParent<VBoxContainer>();
        if (settingsVBox == null)
        {
            return;
        }

        settingsVBox.AddChild(new HSeparator());
        settingsVBox.AddChild(new Label { Text = "Language Extensions" });

        var manageButton = new Button { Text = "Manage Extensions..." };
        manageButton.Pressed += OnManageExtensionsPressed;
        settingsVBox.AddChild(manageButton);
    }

    private void OnManageExtensionsPressed()
    {
        if (_extensionManagerWindow == null)
        {
            _extensionManagerWindow = new ExtensionManagerWindow();
            AddChild(_extensionManagerWindow);
        }

        _extensionManagerWindow.PopupCentered(new Vector2I(540, 380));
    }

    private void OnAboutToPopup()
    {
        _uiScaleSpinBox.Value = Singletons.AppState.IdeSettings.UiScale;
        _debuggerFilePathLineEdit.Text = Singletons.AppState.IdeSettings.DebuggerExecutablePath;
        _debuggerUseSharpDbgCheckButton.ButtonPressed = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg;
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
}
