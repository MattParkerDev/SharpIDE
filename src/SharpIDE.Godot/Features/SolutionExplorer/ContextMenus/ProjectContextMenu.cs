using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.SolutionExplorer;

file enum ProjectContextMenuOptions
{
    CreateNew = 0,
    Run = 1,
    Build = 2,
    Rebuild = 3,
    Clean = 4,
    Restore = 5,
    DotnetUserSecrets = 6,
    Remove = 7,
}

file enum CreateNewSubmenuOptions
{
    Directory = 1,
    CSharpFile = 2
}

public partial class SolutionExplorerPanel
{
    private Texture2D _runIcon = ResourceLoader.Load<Texture2D>("uid://bkty6563cthj8");
    
    [Inject] private readonly BuildService _buildService = null!;
    [Inject] private readonly RunService _runService = null!;
    [Inject] private readonly DotnetUserSecretsService _dotnetUserSecretsService = null!;

    private void OpenContextMenuProject(SharpIdeProjectModel project)
    {
        Guard.Against.Null(project.Folder);
        var menu = new PopupMenu();
        AddChild(menu);
        var createNewSubmenu = new PopupMenu();
        menu.AddSubmenuNodeItem("Add", createNewSubmenu, (int)ProjectContextMenuOptions.CreateNew);
        menu.AddSeparator();
        createNewSubmenu.AddItem("Directory", (int)CreateNewSubmenuOptions.Directory);
        createNewSubmenu.AddItem("C# File", (int)CreateNewSubmenuOptions.CSharpFile);
        createNewSubmenu.IdPressed += id => OnCreateNewSubmenuPressed(id, project.Folder);

        if (project is { IsLoaded: true, IsRunnable: true })
        {
            menu.AddIconItem(_runIcon, "Run", (int)ProjectContextMenuOptions.Run);
            menu.SetItemIconMaxWidth(menu.GetItemIndex((int)ProjectContextMenuOptions.Run), 20);
            menu.AddSeparator();
        }

        menu.AddItem("Build", (int)ProjectContextMenuOptions.Build);
        menu.AddItem("Rebuild", (int)ProjectContextMenuOptions.Rebuild);
        menu.AddItem("Clean", (int)ProjectContextMenuOptions.Clean);
        menu.AddItem("Restore", (int)ProjectContextMenuOptions.Restore);
        menu.AddSeparator();
        menu.AddItem(".NET User Secrets", (int)ProjectContextMenuOptions.DotnetUserSecrets);
        menu.AddSeparator();
        menu.AddItem("Remove", (int)ProjectContextMenuOptions.Remove);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id =>
        {
            var actionId = (ProjectContextMenuOptions)id;
            if (actionId is ProjectContextMenuOptions.Run)
            {
                _ = Task.GodotRun(async () =>
                {
                    await _runService.RunProject(project);
                });
            }
            if (actionId is ProjectContextMenuOptions.Build)
            {
                _ = Task.GodotRun(async () => await MsBuildProject(project, BuildType.Build));
            }
            else if (actionId is ProjectContextMenuOptions.Rebuild)
            {
                _ = Task.GodotRun(async () => await MsBuildProject(project, BuildType.Rebuild));
            }
            else if (actionId is ProjectContextMenuOptions.Clean)
            {
                _ = Task.GodotRun(async () => await MsBuildProject(project, BuildType.Clean));
            }
            else if (actionId is ProjectContextMenuOptions.Restore)
            {
                _ = Task.GodotRun(async () => await MsBuildProject(project, BuildType.Restore));
            }
            else if (actionId is ProjectContextMenuOptions.DotnetUserSecrets)
            {
                _ = Task.GodotRun(async () =>
                {
                    var (userSecretsId, filePath) = await _dotnetUserSecretsService.GetOrCreateUserSecretsId(project);
                    OS.ShellShowInFileManager(filePath);
                });
            }
            else if (actionId is ProjectContextMenuOptions.Remove)
            {
                OpenRemoveProjectConfirmation(project);
            }
        };
			
        var globalMousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }
    private void OpenRemoveProjectConfirmation(SharpIdeProjectModel project)
    {
        Guard.Against.Null(project.Folder);
        var projectFolderContainsSolution = ProjectFolderContainsSolution(project);
        OpenRemoveFromSolutionConfirmation(
            "Remove Project",
            $"Remove project '{project.Name.Value}' from solution?",
            "Also delete project folder and files from the file system",
            projectFolderContainsSolution ? "The project folder contains the solution, so it cannot be deleted" : null,
            async deleteFromFileSystem =>
            {
                await _sharpIdeSolutionService.RemoveProject(project);
                if (deleteFromFileSystem) await _ideFileOperationsService.DeleteDirectory(project.Folder);
            });
    }

    private async Task MsBuildProject(SharpIdeProjectModel project, BuildType buildType)
    {
        await _buildService.MsBuildAsync(project.FilePath, buildType);
    }
}
