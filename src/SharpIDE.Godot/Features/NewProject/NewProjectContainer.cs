using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.DotnetNew;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.NewProject;

public partial class NewProjectContainer : MarginContainer
{
    [Inject] private readonly DotnetTemplateService _dotnetTemplateService = null!;

    public ISolutionOrSolutionFolder ParentNode { get; set; } = null!;
    public bool IsCreating => _templateComponent?.IsCreating ?? false;

    private Label _catalogueStatusLabel = null!;
    private readonly CancellationTokenSource _catalogueCancellation = new();
    private bool _isLoading = true;
    private readonly ButtonGroup _categoryButtonGroup = new();
    private readonly Dictionary<Button, string> _categoryButtons = [];
    private LineEdit _searchLineEdit = null!;
    private VBoxContainer _microsoftTemplatesVBoxContainer = null!;
    private VBoxContainer _customTemplatesVBoxContainer = null!;
    private Label _customTemplatesLabel = null!;
    private TemplateComponent _templateComponent = null!;
    private Dictionary<string, Dictionary<string, List<SdkTemplateVariant>>> _categorisedTemplates = [];
    private Button? _selectedCategoryButton;

    public override void _Ready()
    {
        Guard.Against.Null(ParentNode);
        IExpandableSharpIdeNode root = ParentNode;
        while (root is SharpIdeSolutionFolder folder) root = folder.Parent!;
        var repositoryDirectory = ((SharpIdeSolutionModel)root).DirectoryPath;
        _catalogueStatusLabel = GetNode<Label>("%CatalogueStatusLabel");
        _searchLineEdit = GetNode<LineEdit>("%SearchLineEdit");
        _microsoftTemplatesVBoxContainer = GetNode<VBoxContainer>("%MicrosoftTemplatesVBoxContainer");
        _customTemplatesVBoxContainer = GetNode<VBoxContainer>("%CustomTemplatesVBoxContainer");
        _customTemplatesLabel = GetNode<Label>("%CustomTemplatesLabel");
        _templateComponent = GetNode<TemplateComponent>("%TemplateComponent");
        _templateComponent.ParentNode = ParentNode;
        _templateComponent.Visible = false;
        _customTemplatesLabel.Visible = false;
        _searchLineEdit.TextChanged += OnSearchTextChanged;
        _categoryButtonGroup.Pressed += OnCategoryButtonPressed;
        var cancellationToken = _catalogueCancellation.Token;
        _ = Task.GodotRun(() => LoadTemplates(repositoryDirectory, cancellationToken));
    }

    public override void _ExitTree()
    {
        _catalogueCancellation.Cancel();
        _catalogueCancellation.Dispose();
    }

    private async Task LoadTemplates(string repositoryDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var catalogue = await _dotnetTemplateService.GetRepositoryCatalogue(repositoryDirectory, cancellationToken);
            await this.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                _categorisedTemplates = catalogue.Categories;
                _templateComponent.RepositorySdk = catalogue.RepositorySdk;
                _catalogueStatusLabel.Visible = false;
                foreach (var (categoryName, templatesByGroupIdentity) in _categorisedTemplates)
                {
                    if (templatesByGroupIdentity.Count == 0) continue;
                    var button = new Button { Text = categoryName, ToggleMode = true, ButtonGroup = _categoryButtonGroup };
                    _categoryButtons.Add(button, categoryName);
                    var container = DotnetTemplateService.MicrosoftTemplateCategories.Contains(categoryName)
                        ? _microsoftTemplatesVBoxContainer : _customTemplatesVBoxContainer;
                    container.AddChild(button);
                }
                _isLoading = false;
                OnSearchTextChanged(_searchLineEdit.Text);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the dialog cancels discovery.
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) return;
            GD.PrintErr(ex);
            await this.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                _catalogueStatusLabel.Text = $"Could not load templates for the repository SDK. {ex.Message}";
                _catalogueStatusLabel.Visible = true;
                _isLoading = false;
            });
        }
    }

    private void OnCategoryButtonPressed(BaseButton baseButton)
    {
        if (_isLoading) return;
        if (baseButton is Button button && _categoryButtons.ContainsKey(button)) SelectCategory(button);
    }

    private void OnSearchTextChanged(string searchText)
    {
        if (_isLoading) return;
        foreach (var (button, categoryName) in _categoryButtons)
        {
            button.Visible = _categorisedTemplates[categoryName].Values
                .Any(variants => variants.Any(variant => TemplateSearch.Matches(variant.Template, searchText)));
        }
        _customTemplatesLabel.Visible = _customTemplatesVBoxContainer.GetChildren().Any(child => child is Button { Visible: true });
        if (_selectedCategoryButton is null || !_selectedCategoryButton.Visible)
        {
            var first = _categoryButtons.FirstOrDefault(pair => pair.Key.Visible).Key;
            if (first is null)
            {
                _selectedCategoryButton = null;
                _templateComponent.Visible = false;
            }
            else SelectCategory(first);
        }
        else _templateComponent.SetSearchText(searchText);
    }

    private void SelectCategory(Button categoryButton)
    {
        _selectedCategoryButton = categoryButton;
        if (!categoryButton.ButtonPressed) categoryButton.SetPressed(true);
        _templateComponent.Visible = true;
        _templateComponent.SetTemplates(_categorisedTemplates[_categoryButtons[categoryButton]]);
        _templateComponent.SetSearchText(_searchLineEdit.Text);
    }
}
