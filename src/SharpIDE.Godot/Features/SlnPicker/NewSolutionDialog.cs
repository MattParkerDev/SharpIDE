using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SlnPicker;

public partial class NewSolutionDialog : ConfirmationDialog
{
    private LineEdit _nameLineEdit = null!;
    private LineEdit _directoryLineEdit = null!;
    private Button _browseButton = null!;
    private CheckBox _createDirectoryCheckBox = null!;
    private OptionButton _formatOptionButton = null!;
    private Label _solutionPathLabel = null!;
    private Label _errorLabel = null!;
    private FileDialog _directoryFileDialog = null!;
    private Button _createButton = null!;
    private Button _cancelButton = null!;

    private bool _isCreating;

    /// Called with the new solution's file path once it has been created
    public Action<string>? SolutionCreated { get; set; }

    public override void _Ready()
    {
        _nameLineEdit = GetNode<LineEdit>("%SolutionNameLineEdit");
        _directoryLineEdit = GetNode<LineEdit>("%SolutionDirectoryLineEdit");
        _browseButton = GetNode<Button>("%BrowseButton");
        _createDirectoryCheckBox = GetNode<CheckBox>("%CreateDirectoryCheckBox");
        _formatOptionButton = GetNode<OptionButton>("%FormatOptionButton");
        _solutionPathLabel = GetNode<Label>("%SolutionPathLabel");
        _errorLabel = GetNode<Label>("%ErrorLabel");
        _directoryFileDialog = GetNode<FileDialog>("%DirectoryFileDialog");
        _createButton = GetOkButton();
        _cancelButton = GetCancelButton();
        this.RightAlignButtons();

        _directoryLineEdit.Text = GetDefaultSolutionParentDirectory();
        _nameLineEdit.TextChanged += _ => Validate();
        _directoryLineEdit.TextChanged += _ => Validate();
        _createDirectoryCheckBox.Toggled += _ => Validate();
        _formatOptionButton.ItemSelected += _ => Validate();
        _nameLineEdit.TextSubmitted += _ => OnConfirmed();
        _browseButton.Pressed += () =>
        {
            if (Directory.Exists(_directoryLineEdit.Text.Trim())) _directoryFileDialog.CurrentDir = _directoryLineEdit.Text.Trim();
            _directoryFileDialog.PopupCentered();
        };
        _directoryFileDialog.DirSelected += dir => _directoryLineEdit.Text = dir;
        Confirmed += OnConfirmed;
        Canceled += QueueFree;

        _nameLineEdit.GrabFocus();
        _nameLineEdit.SelectAll();
        Validate();
    }

    private string? GetSolutionFilePath()
    {
        var name = _nameLineEdit.Text.Trim();
        var directory = _directoryLineEdit.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(directory)) return null;
        var extension = _formatOptionButton.GetItemText(_formatOptionButton.Selected);
        var solutionDirectory = _createDirectoryCheckBox.ButtonPressed ? Path.Combine(directory, name) : directory;
        return Path.Combine(solutionDirectory, $"{name}{extension}");
    }

    private void Validate()
    {
        var name = _nameLineEdit.Text.Trim();
        var directory = _directoryLineEdit.Text.Trim();
        var solutionFilePath = GetSolutionFilePath();
        string? error = null;
        if (string.IsNullOrEmpty(name)) error = "Solution name cannot be empty";
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) error = "Solution name contains invalid characters";
        else if (string.IsNullOrEmpty(directory)) error = "Solution directory cannot be empty";
        else if (Path.IsPathFullyQualified(directory) is false) error = "Solution directory must be an absolute path";
        else if (File.Exists(solutionFilePath)) error = "A solution with this name already exists in this directory";

        _solutionPathLabel.Text = solutionFilePath is null ? string.Empty : Path.GetDirectoryName(solutionFilePath);
        _solutionPathLabel.TooltipText = solutionFilePath ?? string.Empty;
        _errorLabel.Text = error ?? string.Empty;
        _errorLabel.Visible = error is not null;
        _createButton.Disabled = error is not null || _isCreating;
        _cancelButton.Disabled = _isCreating;
    }

    private void OnConfirmed()
    {
        Validate();
        if (_createButton.Disabled) return;
        var solutionFilePath = GetSolutionFilePath()!;
        _isCreating = true;
        Validate();
        _ = Task.GodotRun(async () =>
        {
            try
            {
                await SharpIdeSolutionService.CreateSolution(solutionFilePath);
                await this.InvokeAsync(() =>
                {
                    SolutionCreated?.Invoke(solutionFilePath);
                    QueueFree();
                });
            }
            catch (Exception ex)
            {
                GD.PrintErr(ex);
                await this.InvokeAsync(() =>
                {
                    _isCreating = false;
                    Validate();
                    _errorLabel.Text = $"Could not create solution. {ex.Message}";
                    _errorLabel.Visible = true;
                });
            }
        });
    }

    // Recent solutions are usually each in their own repository directory, so default to the parent of the most recent one
    private static string GetDefaultSolutionParentDirectory()
    {
        var lastSln = Singletons.AppState.RecentSlns.LastOrDefault();
        var lastSlnParentDirectory = lastSln is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(lastSln.FilePath));
        if (lastSlnParentDirectory is not null && Directory.Exists(lastSlnParentDirectory)) return lastSlnParentDirectory;
        return System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    }
}
