using Godot;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot.Features.Settings;

public partial class SettingsWindow : Window
{
    private SpinBox _uiScaleSpinBox = null!;
    private LineEdit _debuggerFilePathLineEdit = null!;
    private CheckButton _debuggerUseSharpDbgCheckButton = null!;
    private OptionButton _themeOptionButton = null!;
    private LineEdit _customThemePathLineEdit = null!;
    private FileDialog _customThemeFileDialog = null!;

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

        AddCustomThemeControls();
    }

    private void AddCustomThemeControls()
    {
        // Find the main settings VBox (parent of the first visible control)
        var settingsVBox = _uiScaleSpinBox.GetParent<VBoxContainer>();
        if (settingsVBox == null)
            return;

        // Create a container for custom theme controls
        var customThemeContainer = new VBoxContainer();

        // Label for the group
        var label = new Label { Text = "Custom TextMate Theme" };
        customThemeContainer.AddChild(label);

        // HBox for path input and browse button
        var hBox = new HBoxContainer();

        _customThemePathLineEdit = new LineEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Editable = false // User should use the browse button, not type
        };
        _customThemePathLineEdit.TextChanged += OnCustomThemePathChanged;
        hBox.AddChild(_customThemePathLineEdit);

        var browseButton = new Button
        {
            Text = "Browse…",
            CustomMinimumSize = new Vector2(80, 0)
        };
        browseButton.Pressed += OnBrowseCustomThemeClicked;
        hBox.AddChild(browseButton);

        var clearButton = new Button
        {
            Text = "Clear",
            CustomMinimumSize = new Vector2(60, 0)
        };
        clearButton.Pressed += OnClearCustomTheme;
        hBox.AddChild(clearButton);

        customThemeContainer.AddChild(hBox);

        // Hint label
        var hintLabel = new Label
        {
            Text = "Select a VS Code .json or .tmTheme file. Will fall back to Light/Dark theme.",
            ThemeTypeVariation = "Gray500Label"
        };
        customThemeContainer.AddChild(hintLabel);

        // Add separator before the custom theme section (optional but nice to have)
        var separator = new HSeparator();
        settingsVBox.AddChild(separator);

        // Add custom theme controls to the main VBox
        settingsVBox.AddChild(customThemeContainer);

        // Create file dialog
        _customThemeFileDialog = new FileDialog
        {
            Title = "Select TextMate Theme",
            Filters = new[] { "*.json ; VS Code Theme", "*.tmTheme ; TextMate Theme", "*.*; All Files" }
        };
        _customThemeFileDialog.FileSelected += OnCustomThemeFileSelected;
        AddChild(_customThemeFileDialog);
    }

    private void OnBrowseCustomThemeClicked()
    {
        var currentPath = Singletons.AppState.IdeSettings.CustomThemePath;
        if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
        {
            _customThemeFileDialog.CurrentDir = Path.GetDirectoryName(currentPath) ?? "";
        }
        _customThemeFileDialog.PopupCenteredRatio(0.6f);
    }

    private void OnCustomThemeFileSelected(string path)
    {
        if (File.Exists(path))
        {
            Singletons.AppState.IdeSettings.CustomThemePath = path;
            _customThemePathLineEdit.Text = path;
            ReloadEditorTheme();
        }
    }

    private void OnCustomThemePathChanged(string newText)
    {
        // LineEdit is read-only, so this shouldn't normally be called
        // But if it is (e.g., programmatically), update the setting
        if (File.Exists(newText))
        {
            Singletons.AppState.IdeSettings.CustomThemePath = newText;
            ReloadEditorTheme();
        }
    }

    private void OnClearCustomTheme()
    {
        Singletons.AppState.IdeSettings.CustomThemePath = null;
        _customThemePathLineEdit.Text = "";
        ReloadEditorTheme();
    }

    private void ReloadEditorTheme()
    {
        // Trigger theme change event with current theme setting
        GodotGlobalEvents.Instance.TextEditorThemeChanged.InvokeParallelFireAndForget(
            Singletons.AppState.IdeSettings.Theme);
    }

    private void OnAboutToPopup()
    {
        _uiScaleSpinBox.Value = Singletons.AppState.IdeSettings.UiScale;
        _debuggerFilePathLineEdit.Text = Singletons.AppState.IdeSettings.DebuggerExecutablePath;
        _debuggerUseSharpDbgCheckButton.ButtonPressed = Singletons.AppState.IdeSettings.DebuggerUseSharpDbg;
        var themeOptionIndex = _themeOptionButton.GetOptionIndexOrNullForString(Singletons.AppState.IdeSettings.Theme.ToString());
        if (themeOptionIndex is not null) _themeOptionButton.Selected = themeOptionIndex.Value;
        _customThemePathLineEdit.Text = Singletons.AppState.IdeSettings.CustomThemePath ?? "";
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