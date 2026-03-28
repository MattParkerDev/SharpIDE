using Godot;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.LanguageExtensions;

namespace SharpIDE.Godot.Features.ExtensionManager;

/// <summary>
/// Popup window for installing and uninstalling VS 2022 language extensions (.vsix).
/// Uses programmatic UI — no .tscn required.
/// </summary>
public partial class ExtensionManagerWindow : Window
{
    [Inject] private readonly ExtensionInstaller _extensionInstaller = null!;
    [Inject] private readonly LanguageExtensionRegistry _languageExtensionRegistry = null!;
    [Inject] private readonly ILogger<ExtensionManagerWindow> _logger = null!;

    private ItemList _extensionList = null!;
    private Button _installButton = null!;
    private Button _uninstallButton = null!;
    private Label _statusLabel = null!;
    private FileDialog _vsixFileDialog = null!;

    public override void _Ready()
    {
        Title = "Language Extensions";
        MinSize = new Vector2I(520, 360);
        CloseRequested += Hide;

        BuildUi();
        PopulateList();
    }

    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(margin);

        var vbox = new VBoxContainer();
        margin.AddChild(vbox);

        var headerLabel = new Label { Text = "Installed Language Extensions" };
        vbox.AddChild(headerLabel);

        _extensionList = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200)
        };
        _extensionList.ItemSelected += _ => UpdateButtonStates();
        vbox.AddChild(_extensionList);

        var buttonRow = new HBoxContainer();
        vbox.AddChild(buttonRow);

        _installButton = new Button { Text = "Install (.vsix)…" };
        _installButton.Pressed += OnInstallPressed;
        buttonRow.AddChild(_installButton);

        _uninstallButton = new Button { Text = "Uninstall" };
        _uninstallButton.Pressed += OnUninstallPressed;
        _uninstallButton.Disabled = true;
        buttonRow.AddChild(_uninstallButton);

        _statusLabel = new Label
        {
            Text = string.Empty,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        vbox.AddChild(_statusLabel);

        // File dialog for picking .vsix
        _vsixFileDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Select VS 2022 Language Extension"
        };
        _vsixFileDialog.AddFilter("*.vsix", "VS 2022 Extension");
        _vsixFileDialog.FileSelected += OnVsixFileSelected;
        AddChild(_vsixFileDialog);
    }

    private void PopulateList()
    {
        _extensionList.Clear();
        foreach (var ext in _languageExtensionRegistry.GetAllExtensions())
        {
            var extensions = ext.Languages.SelectMany(l => l.FileExtensions).Distinct().ToList();
            var extLabel = extensions.Count > 0 ? string.Join(", ", extensions) : "no file types";
            _extensionList.AddItem($"{ext.DisplayName} v{ext.Version}  [{extLabel}]");
        }
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        _uninstallButton.Disabled = _extensionList.GetSelectedItems().Length == 0;
    }

    private void OnInstallPressed()
    {
        _vsixFileDialog.PopupCentered(new Vector2I(700, 400));
    }

    private void OnVsixFileSelected(string path)
    {
        _installButton.Disabled = true;
        _statusLabel.Text = $"Installing {System.IO.Path.GetFileName(path)}…";

        _ = System.Threading.Tasks.Task.GodotRun(async () =>
        {
            try
            {
                var installed = await System.Threading.Tasks.Task.Run(() => _extensionInstaller.Install(path));
                await this.InvokeAsync(() =>
                {
                    _statusLabel.Text = $"Installed '{installed.DisplayName}' successfully.";
                    PopulateList();
                    _installButton.Disabled = false;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Extension install failed for {Path}", path);
                await this.InvokeAsync(() =>
                {
                    _statusLabel.Text = $"Install failed: {ex.Message}";
                    _installButton.Disabled = false;
                });
            }
        });
    }

    private void OnUninstallPressed()
    {
        var selected = _extensionList.GetSelectedItems();
        if (selected.Length == 0) return;

        var index = selected[0];
        var extensions = _languageExtensionRegistry.GetAllExtensions();
        if (index >= extensions.Count) return;

        var extensionId = extensions[index].Id;
        try
        {
            _extensionInstaller.Uninstall(extensionId);
            _statusLabel.Text = $"Uninstalled '{extensions[index].DisplayName}'.";
            PopulateList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extension uninstall failed for {Id}", extensionId);
            _statusLabel.Text = $"Uninstall failed: {ex.Message}";
        }
    }
}
