using Godot;
using Microsoft.TemplateEngine.Abstractions;
using SharpIDE.Application.Features.DotnetNew;

namespace SharpIDE.Godot.Features.NewProject;

public partial class TemplateComponent : VBoxContainer
{
    public string DefaultNewProjectParentPath { get; set; } = null!;
    
    private LineEdit _projectNameLineEdit = null!;
    private LineEdit _projectDirectoryLineEdit = null!;
    private Label _templateTypeLabel = null!;
    private Label _projectDirectoryAndProjectNameLabel = null!;
    private ItemList _templatesItemList = null!;

    private Label _templateShortNameLabel = null!;
    private Label _templateIdentityLabel = null!;
    private Label _templateGroupIdentityLabel = null!;
    private Label _templateAuthorLabel = null!;
    private Label _templateClassificationsLabel = null!;
    
    private Button _createTemplateButton = null!;
    private Button _cancelButton = null!;

    private Dictionary<string, List<ITemplateInfo>> _templatesForCurrentCategory = null!;
    private ITemplateInfo _selectedTemplate = null!;
    
    [Inject] DotnetTemplateService _dotnetTemplateService = null!;

    public override void _Ready()
    {
        _projectNameLineEdit = GetNode<LineEdit>("%ProjectNameLineEdit");
        _projectDirectoryLineEdit = GetNode<LineEdit>("%ProjectDirectoryLineEdit");
        _projectDirectoryAndProjectNameLabel = GetNode<Label>("%ProjectDirectoryAndProjectNameLabel");
        _templateTypeLabel = GetNode<Label>("%TemplateTypeLabel");
        _templatesItemList = GetNode<ItemList>("%TemplatesItemList");
        
        _templateShortNameLabel = GetNode<Label>("%TemplateShortNameLabel");
        _templateIdentityLabel = GetNode<Label>("%TemplateIdentityLabel");
        _templateGroupIdentityLabel = GetNode<Label>("%TemplateGroupIdentityLabel");
        _templateAuthorLabel = GetNode<Label>("%TemplateAuthorLabel");
        _templateClassificationsLabel = GetNode<Label>("%TemplateClassificationsLabel");
        
        _createTemplateButton = GetNode<Button>("%CreateTemplateButton");
        _cancelButton = GetNode<Button>("%CancelButton");
        _cancelButton.Pressed += () =>
        {
            GetWindow().QueueFree();
        };
        _createTemplateButton.Pressed += () =>
        {
            var projectName = _projectNameLineEdit.Text;
            var path = _projectDirectoryLineEdit.Text;
            _ = Task.GodotRun(async () =>
            {
                await _dotnetTemplateService.ExecuteTemplate(_selectedTemplate, projectName, path, []);
            });
            GetWindow().QueueFree();
        };
    }

    public void SetTemplates(Dictionary<string, List<ITemplateInfo>> templatesForCategory)
    {
        _templatesForCurrentCategory = templatesForCategory;
        var defaultTemplate = _templatesForCurrentCategory.First().Value.First();
        SetSelectedTemplate(defaultTemplate);
    }

    private void SetSelectedTemplate(ITemplateInfo selectedTemplate)
    {
        _selectedTemplate = selectedTemplate;
        var defaultProjectName = selectedTemplate.DefaultName;
        defaultProjectName ??= "Project1";
        _projectNameLineEdit.Text = defaultProjectName;
        _projectDirectoryLineEdit.Text = DefaultNewProjectParentPath;
        _projectDirectoryAndProjectNameLabel.Text = Path.Combine(DefaultNewProjectParentPath, defaultProjectName);
        _templateTypeLabel.Text = selectedTemplate.Name;
        _templatesItemList.Clear();
        
        foreach (var template in _templatesForCurrentCategory)
        {
            _templatesItemList.AddItem(template.Value.First().Name);
        }

        List<string> skippedParameterNames = ["TargetFrameworkOverride", "Framework", "langVersion", "skipRestore"]; 
        foreach (var parameter in selectedTemplate.ParameterDefinitions.Where(s => s.Precedence.PrecedenceDefinition is not PrecedenceDefinition.Implicit && skippedParameterNames.Contains(s.Name) is false))
        {
            ;
        }
        
        _templateShortNameLabel.Text = string.Join("; ", selectedTemplate.ShortNameList);
        _templateIdentityLabel.Text = selectedTemplate.Identity;
        _templateGroupIdentityLabel.Text = selectedTemplate.GroupIdentity;
        _templateAuthorLabel.Text = selectedTemplate.Author;
        _templateClassificationsLabel.Text = string.Join("; ", selectedTemplate.Classifications);
    }
}