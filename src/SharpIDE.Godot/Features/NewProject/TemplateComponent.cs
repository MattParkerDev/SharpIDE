using Ardalis.GuardClauses;
using Godot;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Edge.Template;
using SharpIDE.Application.Features.DotnetNew;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.NewProject;

public partial class TemplateComponent : VBoxContainer
{
    public ISolutionOrSolutionFolder ParentNode { get; set; } = null!;
    public bool IsCreating => _isCreating;
    public DotnetTemplateSdk RepositorySdk { get; set; } = null!;
    private SdkTemplateVariant? _selectedVariant;
    private OptionButton _sdkOptionButton = null!;
    private Label _sdkLabel = null!;
    private Control _frameworkControls = null!;
    private readonly Dictionary<string, SdkTemplateVariant> _selectedVariants = [];

    private LineEdit _projectNameLineEdit = null!;
    private LineEdit _projectDirectoryLineEdit = null!;
    private Label _projectDirectoryAndProjectNameLabel = null!;
    private Label _templateTypeLabel = null!;
    private ItemList _templatesItemList = null!;
    private Control _targetFrameworkContainer = null!;
    private OptionButton _targetFrameworkOptionButton = null!;
    private VBoxContainer _templateParametersVBoxContainer = null!;
    private FoldableContainer _advancedSettingsContainer = null!;
    private VBoxContainer _advancedParametersVBoxContainer = null!;


    private Label _templateShortNameLabel = null!;
    private Label _templateIdentityLabel = null!;
    private Label _templateGroupIdentityLabel = null!;
    private Label _templateAuthorLabel = null!;
    private Label _templateClassificationsLabel = null!;
    private Label _statusLabel = null!;

    private Button _createTemplateButton = null!;
    private Button _cancelButton = null!;

    private Dictionary<string, List<SdkTemplateVariant>> _templatesForCurrentCategory = [];
    private readonly List<ITemplateInfo> _visibleTemplates = [];
    private readonly Dictionary<string, Func<string?>> _parameterValueGetters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TemplateParameterInput> _parameterInputs = new(StringComparer.OrdinalIgnoreCase);
    private ITemplateInfo? _selectedTemplate;
    private string _searchText = string.Empty;
    private bool _isCreating;

    [Inject] private readonly DotnetTemplateService _dotnetTemplateService = null!;
    [Inject] private readonly SharpIdeSolutionService _sharpIdeSolutionService = null!;

    public override void _Ready()
    {
        _projectNameLineEdit = GetNode<LineEdit>("%ProjectNameLineEdit");
        _projectDirectoryLineEdit = GetNode<LineEdit>("%ProjectDirectoryLineEdit");
        _projectDirectoryAndProjectNameLabel = GetNode<Label>("%ProjectDirectoryAndProjectNameLabel");
        _templateTypeLabel = GetNode<Label>("%TemplateTypeLabel");
        _templatesItemList = GetNode<ItemList>("%TemplatesItemList");
        _targetFrameworkContainer = GetNode<Control>("%TargetFrameworkContainer");
        _frameworkControls = GetNode<Control>("%FrameworkControls");
        _sdkOptionButton = GetNode<OptionButton>("%TemplateSdkOptionButton");
        _sdkLabel = GetNode<Label>("%TemplateSdkLabel");
        _sdkOptionButton.ItemSelected += OnSdkSelected;
        _targetFrameworkOptionButton = GetNode<OptionButton>("%TargetFrameworkOptionButton");
        _templateParametersVBoxContainer = GetNode<VBoxContainer>("%TemplateParametersVBoxContainer");
        _advancedSettingsContainer = GetNode<FoldableContainer>("%AdvancedSettingsContainer");
        _advancedParametersVBoxContainer = GetNode<VBoxContainer>("%AdvancedParametersVBoxContainer");
        _statusLabel = GetNode<Label>("%StatusLabel");

        _templateShortNameLabel = GetNode<Label>("%TemplateShortNameLabel");
        _templateIdentityLabel = GetNode<Label>("%TemplateIdentityLabel");
        _templateGroupIdentityLabel = GetNode<Label>("%TemplateGroupIdentityLabel");
        _templateAuthorLabel = GetNode<Label>("%TemplateAuthorLabel");
        _templateClassificationsLabel = GetNode<Label>("%TemplateClassificationsLabel");

        _createTemplateButton = GetNode<Button>("%CreateTemplateButton");
        _cancelButton = GetNode<Button>("%CancelButton");

        _templatesItemList.ItemSelected += OnTemplateItemSelected;
        _projectDirectoryLineEdit.TextChanged += ProjectNameOrDirectoryChanged;
        _projectNameLineEdit.TextChanged += ProjectNameOrDirectoryChanged;
        _cancelButton.Pressed += () =>
        {
            GetWindow().QueueFree();
        };
        _createTemplateButton.Pressed += OnCreateTemplatePressed;
    }

    public void SetTemplates(Dictionary<string, List<SdkTemplateVariant>> templatesForCategory)
    {
        _templatesForCurrentCategory = templatesForCategory;
        RebuildTemplateList();
    }

    public void SetSearchText(string searchText)
    {
        _searchText = searchText;
        RebuildTemplateList();
    }

    private void RebuildTemplateList()
    {
        var previouslySelectedGroupIdentity = _selectedTemplate is null ? null : GetGroupIdentity(_selectedTemplate);

        _templatesItemList.Clear();
        _visibleTemplates.Clear();

        foreach (var templates in _templatesForCurrentCategory.Values)
        {
            var template = templates.FirstOrDefault()?.Template;
            if (template is null || !templates.Any(variant => TemplateSearch.Matches(variant.Template, _searchText))) continue;

            _visibleTemplates.Add(template);
            _templatesItemList.AddItem(template.Name);
        }

        if (_visibleTemplates.Count is 0)
        {
            ClearTemplateDetails();
            return;
        }

        var selectedIndex = previouslySelectedGroupIdentity is null
            ? -1
            : _visibleTemplates.FindIndex(t => GetGroupIdentity(t) == previouslySelectedGroupIdentity);
        if (selectedIndex < 0) selectedIndex = 0;

        _templatesItemList.Select(selectedIndex);
        SelectTemplateGroup(_visibleTemplates[selectedIndex]);
    }

    private void OnTemplateItemSelected(long index)
    {
        if (index < 0 || index >= _visibleTemplates.Count) return;
        SelectTemplateGroup(_visibleTemplates[(int)index]);
    }

    private static string GetGroupIdentity(ITemplateInfo template) => template.GroupIdentity ?? template.Identity;

    private void SelectTemplateGroup(ITemplateInfo template)
    {
        var identity = GetGroupIdentity(template);
        var variants = _templatesForCurrentCategory[identity];
        var variant = _selectedVariants.GetValueOrDefault(identity) ?? variants[0];
        _sdkOptionButton.Clear();
        foreach (var candidate in variants)
        {
            var label = candidate.IsSdkTemplate ? $"from SDK {candidate.Sdk.Version}" : $"Custom template (SDK {candidate.Sdk.Version})";
            if (candidate.Sdk == RepositorySdk) label += " — repository";
            _sdkOptionButton.AddItem(label);
        }
        _sdkOptionButton.Select(variants.IndexOf(variant));
        _sdkOptionButton.Visible = variants.Count > 1;
        _sdkOptionButton.Disabled = _isCreating;
        _sdkLabel.Visible = variants.Count == 1;
        _sdkLabel.Text = _sdkOptionButton.GetItemText(0);
        _selectedVariant = variant;
        SetSelectedTemplate(variant.Template);
    }

    private void OnSdkSelected(long index)
    {
        if (_selectedTemplate is null || _selectedVariant is null) return;
        var identity = GetGroupIdentity(_selectedTemplate);
        var variants = _templatesForCurrentCategory[identity];
        if (_isCreating)
        {
            _sdkOptionButton.Select(variants.IndexOf(_selectedVariant));
            return;
        }
        if (index < 0 || index >= variants.Count) return;
        _selectedVariant = variants[(int)index];
        _selectedVariants[identity] = _selectedVariant;
        SetSelectedTemplate(_selectedVariant.Template, preserveProjectName: true);
    }

    private void SetSelectedTemplate(ITemplateInfo selectedTemplate, bool preserveProjectName = false)
    {
        if (ReferenceEquals(_selectedTemplate, selectedTemplate))
        {
            _createTemplateButton.Disabled = _isCreating;
            return;
        }

        _selectedTemplate = selectedTemplate;

        var defaultProjectName = selectedTemplate.DefaultName;
        if (string.IsNullOrWhiteSpace(defaultProjectName)) defaultProjectName = "Project1";
        if (!preserveProjectName) _projectNameLineEdit.Text = defaultProjectName;

        if (string.IsNullOrWhiteSpace(_projectDirectoryLineEdit.Text))
        {
            _projectDirectoryLineEdit.Text = GetDefaultNewProjectParentPath(ParentNode);
        }
        UpdateProjectPathLabel();

        _templateTypeLabel.Text = selectedTemplate.Name;
        _templateShortNameLabel.Text = string.Join("; ", selectedTemplate.ShortNameList);
        _templateIdentityLabel.Text = selectedTemplate.Identity;
        _templateGroupIdentityLabel.Text = selectedTemplate.GroupIdentity;
        _templateAuthorLabel.Text = selectedTemplate.Author;
        _templateClassificationsLabel.Text = string.Join("; ", selectedTemplate.Classifications);

        PopulateTargetFramework(selectedTemplate);
        RebuildParameterControls(selectedTemplate);

        HideStatus();
        _createTemplateButton.Disabled = _isCreating;
    }

    private void PopulateTargetFramework(ITemplateInfo selectedTemplate)
    {
        _targetFrameworkOptionButton.Clear();

        var frameworkParameter = selectedTemplate.ParameterDefinitions
            .FirstOrDefault(p => string.Equals(p.Name, "Framework", StringComparison.OrdinalIgnoreCase) && p.Choices is { Count: > 0 });

        if (frameworkParameter is null)
        {
            _targetFrameworkContainer.Visible = true;
            _frameworkControls.Visible = false;
            return;
        }

        _targetFrameworkContainer.Visible = true;
        _frameworkControls.Visible = true;
        PopulateChoiceOptionButton(_targetFrameworkOptionButton, frameworkParameter);
    }

    private void RebuildParameterControls(ITemplateInfo selectedTemplate)
    {
        ClearParameterControls();

        // These are either surfaced separately above, or are not useful for this dialog.
        HashSet<string> skippedParameterNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "TargetFrameworkOverride", "Framework", "langVersion", "skipRestore"
        };

        foreach (var parameter in selectedTemplate.ParameterDefinitions)
        {
            if (parameter.Precedence.PrecedenceDefinition is PrecedenceDefinition.Implicit or PrecedenceDefinition.Disabled) continue;
            if (skippedParameterNames.Contains(parameter.Name)) continue;

            var container = IsAdvancedParameter(selectedTemplate, parameter) ? _advancedParametersVBoxContainer : _templateParametersVBoxContainer;
            AddParameterControl(parameter, container);
            _parameterInputs[parameter.Name] = new TemplateParameterInput(_parameterValueGetters[parameter.Name](),
                parameter.Precedence.PrecedenceDefinition is PrecedenceDefinition.Required or PrecedenceDefinition.ConditionalyRequired,
                BlankUsesDefault: parameter.Choices is not { Count: > 0 });
        }

        _templateParametersVBoxContainer.Visible = _templateParametersVBoxContainer.GetChildCount() > 0;
        _advancedSettingsContainer.Visible = _advancedParametersVBoxContainer.GetChildCount() > 0;
        _advancedSettingsContainer.Folded = true;
    }

    private bool IsAdvancedParameter(ITemplateInfo template, ITemplateParameter parameter)
    {
        // Required inputs must remain discoverable, including custom template parameters.
        if (parameter.Precedence.PrecedenceDefinition is PrecedenceDefinition.Required or PrecedenceDefinition.ConditionalyRequired) return false;
        return !_dotnetTemplateService.IsParameterVisible(template, parameter.Name);
    }

    private void ClearParameterControls()
    {
        foreach (var container in new[] { _templateParametersVBoxContainer, _advancedParametersVBoxContainer })
        {
            foreach (var child in container.GetChildren())
            {
                container.RemoveChild(child);
                child.QueueFree();
            }
        }
        _advancedSettingsContainer.Visible = false;
        _parameterValueGetters.Clear();
        _parameterInputs.Clear();
    }

    private void AddParameterControl(ITemplateParameter parameter, VBoxContainer container)
    {
        if (parameter.Choices is { Count: > 0 })
        {
            if (parameter.AllowMultipleValues)
            {
                AddMultiChoiceParameterControl(parameter, container);
            }
            else
            {
                var optionButton = new OptionButton();
                PopulateChoiceOptionButton(optionButton, parameter);
                container.AddChild(CreateLabeledParameterRow(parameter, optionButton));
                _parameterValueGetters[parameter.Name] = () => optionButton.GetSelectedMetadata().AsString();
            }
            return;
        }

        if (string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.DataType, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            var checkBox = new CheckBox
            {
                Text = GetParameterDisplayName(parameter),
                ButtonPressed = bool.TryParse(parameter.DefaultValue, out var defaultValue) && defaultValue,
                TooltipText = parameter.Description
            };
            container.AddChild(checkBox);
            _parameterValueGetters[parameter.Name] = () => checkBox.ButtonPressed ? "true" : "false";
            return;
        }

        // Leave optional text/numeric inputs unset so the engine owns default resolution.
        var isRequired = parameter.Precedence.PrecedenceDefinition is PrecedenceDefinition.Required or PrecedenceDefinition.ConditionalyRequired;
        var lineEdit = new LineEdit
        {
            Text = isRequired ? parameter.DefaultValue ?? string.Empty : string.Empty,
        };
        container.AddChild(CreateLabeledParameterRow(parameter, lineEdit));
        _parameterValueGetters[parameter.Name] = () => lineEdit.Text;
    }

    private void AddMultiChoiceParameterControl(ITemplateParameter parameter, VBoxContainer parentContainer)
    {
        var container = new VBoxContainer();
        container.AddChild(CreateParameterLabel(parameter));

        var defaultValues = (parameter.DefaultValue ?? string.Empty)
            .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(string Value, CheckBox CheckBox)> checkBoxes = [];

        foreach (var (choiceValue, choice) in parameter.Choices!)
        {
            var checkBox = new CheckBox
            {
                Text = string.IsNullOrWhiteSpace(choice.DisplayName) ? choiceValue : choice.DisplayName,
                ButtonPressed = defaultValues.Contains(choiceValue),
                TooltipText = choice.Description
            };
            container.AddChild(checkBox);
            checkBoxes.Add((choiceValue, checkBox));
        }

        parentContainer.AddChild(container);
        _parameterValueGetters[parameter.Name] = () => string.Join('|', checkBoxes.Where(x => x.CheckBox.ButtonPressed).Select(x => x.Value));
    }

    private static HBoxContainer CreateLabeledParameterRow(ITemplateParameter parameter, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(CreateParameterLabel(parameter));
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (string.IsNullOrWhiteSpace(parameter.Description) is false) control.TooltipText = parameter.Description;
        row.AddChild(control);
        return row;
    }

    private static Label CreateParameterLabel(ITemplateParameter parameter)
    {
        return new Label
        {
            Text = $"{GetParameterDisplayName(parameter)}: ",
            ThemeTypeVariation = "Gray700Label",
            TooltipText = parameter.Description
        };
    }

    private static string GetParameterDisplayName(ITemplateParameter parameter)
    {
        // Display names use underscores to mark keyboard shortcuts in the dotnet CLI.
        return string.IsNullOrWhiteSpace(parameter.DisplayName)
            ? parameter.Name
            : parameter.DisplayName.Replace("_", string.Empty);
    }

    private static void PopulateChoiceOptionButton(OptionButton optionButton, ITemplateParameter parameter)
    {
        optionButton.Clear();
        if (parameter.Choices is null) return;

        var selectedIndex = 0;
        var index = 0;
        foreach (var (choiceValue, choice) in parameter.Choices)
        {
            optionButton.AddItem(string.IsNullOrWhiteSpace(choice.DisplayName) ? choiceValue : choice.DisplayName, index);
            optionButton.SetItemMetadata(index, choiceValue);
            if (string.IsNullOrWhiteSpace(choice.Description) is false) optionButton.SetItemTooltip(index, choice.Description);

            if (string.Equals(choiceValue, parameter.DefaultValue, StringComparison.OrdinalIgnoreCase)) selectedIndex = index;
            index++;
        }

        if (optionButton.ItemCount > 0) optionButton.Select(selectedIndex);
    }

    private void ClearTemplateDetails()
    {
        _selectedTemplate = null;
        _selectedVariant = null;
        _templateTypeLabel.Text = "No matching templates";
        _templateShortNameLabel.Text = string.Empty;
        _templateIdentityLabel.Text = string.Empty;
        _templateGroupIdentityLabel.Text = string.Empty;
        _templateAuthorLabel.Text = string.Empty;
        _templateClassificationsLabel.Text = string.Empty;

        _targetFrameworkOptionButton.Clear();
        _targetFrameworkContainer.Visible = false;
        ClearParameterControls();
        _templateParametersVBoxContainer.Visible = false;
        HideStatus();
        _createTemplateButton.Disabled = true;
    }

    private void OnCreateTemplatePressed()
    {
        if (_isCreating) return;

        var projectName = _projectNameLineEdit.Text.Trim();
        var projectDirectory = _projectDirectoryLineEdit.Text.Trim();

        if (string.IsNullOrWhiteSpace(projectName) || projectName is "." or "..")
        {
            ShowStatus("Project name is required.");
            return;
        }
        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowStatus("Project name contains invalid characters.");
            return;
        }
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            ShowStatus("Project directory is required.");
            return;
        }
        if (_selectedTemplate is null || _selectedVariant is null)
        {
            ShowStatus("Select a template.");
            return;
        }

        var selectedTemplate = _selectedTemplate;
        var sdk = _selectedVariant.Sdk;
        var path = Path.Combine(projectDirectory, projectName);
        var parameters = _parameterValueGetters
            .Select(pair => (pair.Key, Value: _parameterInputs[pair.Key].GetOverride(pair.Value())))
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var targetFramework = _targetFrameworkOptionButton.ItemCount > 0
            ? _targetFrameworkOptionButton.GetSelectedMetadata().AsString()
            : null;
        if (string.IsNullOrEmpty(targetFramework) is false) parameters["Framework"] = targetFramework;

        _isCreating = true;
        _createTemplateButton.Disabled = true;
        _cancelButton.Disabled = true;
        _sdkOptionButton.Disabled = true;
        HideStatus();

        _ = Task.GodotRun(async () =>
        {
            var succeeded = false;
            try
            {
                var templateCreationResult = await _dotnetTemplateService.ExecuteTemplate(sdk, selectedTemplate, projectName, path, parameters);
                if (templateCreationResult.Status is not CreationResultStatus.Success)
                {
                    await this.InvokeAsync(() => ShowStatus(templateCreationResult.ErrorMessage ?? "Failed to create the project."));
                    return;
                }

                var outputDirectory = Path.GetFullPath(templateCreationResult.OutputBaseDirectory ?? path);
                var projectFilePaths = templateCreationResult.CreationResult?.PrimaryOutputs
                    .Select(output => Path.GetFullPath(output.Path, outputDirectory))
                    .Where(output => Path.GetExtension(output).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(output).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(output).Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToArray() ?? [];

                if (projectFilePaths.Length == 0)
                {
                    await this.InvokeAsync(() => ShowStatus("The template created files but reported no project outputs. Add the generated projects to the solution manually."));
                    return;
                }

                var missingProjectFilePath = projectFilePaths.FirstOrDefault(projectPath => !File.Exists(projectPath));
                if (missingProjectFilePath is not null)
                {
                    await this.InvokeAsync(() => ShowStatus($"The template reported a project that does not exist: {missingProjectFilePath}"));
                    return;
                }

                await Task.Delay(300); // Wait for the files to be added to the SharpIdeRootFolder, before reloading the sln
                foreach (var projectFilePath in projectFilePaths)
                {
                    await _sharpIdeSolutionService.AddProject(ParentNode, Path.GetFileNameWithoutExtension(projectFilePath), projectFilePath);
                }
                succeeded = true;
            }
            catch (Exception ex)
            {
                GD.PrintErr(ex);
                await this.InvokeAsync(() => ShowStatus(ex.Message));
            }
            finally
            {
                if (succeeded is false)
                {
                    await this.InvokeAsync(() =>
                    {
                        _isCreating = false;
                        _createTemplateButton.Disabled = _selectedTemplate is null;
                        _cancelButton.Disabled = false;
                        _sdkOptionButton.Disabled = false;
                    });
                }
            }

            if (succeeded)
            {
                await this.InvokeAsync(() => GetWindow().QueueFree());
            }
        });
    }

    private void ShowStatus(string message)
    {
        _statusLabel.Text = message;
        _statusLabel.Visible = true;
    }

    private void HideStatus()
    {
        _statusLabel.Text = string.Empty;
        _statusLabel.Visible = false;
    }

    private void ProjectNameOrDirectoryChanged(string _)
    {
        UpdateProjectPathLabel();
    }

    private void UpdateProjectPathLabel()
    {
        _projectDirectoryAndProjectNameLabel.Text = Path.Combine(_projectDirectoryLineEdit.Text, _projectNameLineEdit.Text);
    }

    private static string GetDefaultNewProjectParentPath(ISolutionOrSolutionFolder parentNode)
    {
        List<string> folderNames = [];
        IExpandableSharpIdeNode node = parentNode;
        while (node is SharpIdeSolutionFolder slnFolderNode)
        {
            folderNames.Add(slnFolderNode.Name);
            node = slnFolderNode.Parent!;
        }
        var slnNode = (SharpIdeSolutionModel)node;
        folderNames.Reverse();
        var idealPath = Path.Combine([slnNode.DirectoryPath, ..folderNames]);
        if (Directory.Exists(idealPath)) return idealPath;
        var directoryInfo = new DirectoryInfo(idealPath);
        while (!directoryInfo.Exists && directoryInfo.FullName != slnNode.DirectoryPath)
        {
            directoryInfo = directoryInfo.Parent!;
        }
        return directoryInfo.FullName;
    }
}

internal static class TemplateSearch
{
    public static bool Matches(ITemplateInfo template, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return true;

        searchText = searchText.Trim();
        return GetSearchableValues(template).Any(value => value.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetSearchableValues(ITemplateInfo template)
    {
        yield return template.Name;
        if (template.GroupIdentity is { } groupIdentity) yield return groupIdentity;
        if (template.Description is { } description) yield return description;

        foreach (var shortName in template.ShortNameList) yield return shortName;
        foreach (var classification in template.Classifications) yield return classification;
    }
}
