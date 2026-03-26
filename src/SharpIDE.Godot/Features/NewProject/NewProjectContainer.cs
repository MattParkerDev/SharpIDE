using Godot;
using SharpIDE.Application.Features.DotnetNew;

namespace SharpIDE.Godot.Features.NewProject;

public partial class NewProjectContainer : VBoxContainer
{
    [Inject] private readonly DotnetTemplateService _dotnetTemplateService = null!;

    public override void _Ready()
    {
        _ = Task.GodotRun(AsyncReady);
    }

    private async Task AsyncReady()
    {
        var categorisedTemplates = await _dotnetTemplateService.GetCategorisedTemplates();
        
    }
}