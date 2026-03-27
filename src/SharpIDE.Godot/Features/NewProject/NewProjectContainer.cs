using Ardalis.GuardClauses;
using Godot;
using Microsoft.TemplateEngine.Abstractions;
using SharpIDE.Application.Features.DotnetNew;

namespace SharpIDE.Godot.Features.NewProject;

public partial class NewProjectContainer : VBoxContainer
{
    [Inject] private readonly DotnetTemplateService _dotnetTemplateService = null!;

    public string DefaultNewProjectParentPath { get; set; } = null!;
    
    private ButtonGroup _categoryButtonGroup = new ButtonGroup();

    private VBoxContainer _microsoftTemplatesVBoxContainer = null!;
    private VBoxContainer _customTemplatesVBoxContainer = null!;
    private TemplateComponent _templateComponent = null!;
    
    private Dictionary<string, Dictionary<string, List<ITemplateInfo>>> _categorisedTemplates = null!;

    public override void _Ready()
    {
        Guard.Against.NullOrWhiteSpace(DefaultNewProjectParentPath);
        _microsoftTemplatesVBoxContainer = GetNode<VBoxContainer>("%MicrosoftTemplatesVBoxContainer");
        _customTemplatesVBoxContainer = GetNode<VBoxContainer>("%CustomTemplatesVBoxContainer");
        _templateComponent = GetNode<TemplateComponent>("%TemplateComponent");
        _templateComponent.DefaultNewProjectParentPath = DefaultNewProjectParentPath;
        _templateComponent.Visible = false; // Hide until the categories are loaded, to select an entry
        _categoryButtonGroup.Pressed += baseButton =>
        {
            var button = (Button)baseButton;
            var templatesForCategory = _categorisedTemplates[button.Text];
            _templateComponent.SetTemplates(templatesForCategory);
        };
        _ = Task.GodotRun(AsyncReady);
    }

    private async Task AsyncReady()
    {
        _categorisedTemplates = await _dotnetTemplateService.GetCategorisedTemplates();
        await this.InvokeAsync(() =>
        {
            foreach (var (categoryName, templatesByGroupIdentity) in _categorisedTemplates)
            {
                if (templatesByGroupIdentity.Count is 0) continue;
                if (DotnetTemplateService.MicrosoftTemplateCategories.Contains(categoryName))
                {
                    var categoryButton = new Button
                    {
                        Text = categoryName,
                        ToggleMode = true,
                        ButtonGroup = _categoryButtonGroup
                    };
                    _microsoftTemplatesVBoxContainer.AddChild(categoryButton);
                }
            }

            var firstCategoryButton = _microsoftTemplatesVBoxContainer.GetChildOrNull<Button?>(0);
            if (firstCategoryButton is not null)
            {
                firstCategoryButton.SetPressed(true);
                _templateComponent.Visible = true;
            }
        });
    }
}