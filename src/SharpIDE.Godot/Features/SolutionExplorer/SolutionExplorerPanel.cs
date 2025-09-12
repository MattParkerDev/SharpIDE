using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel : Panel
{
	[Signal]
	public delegate void FileSelectedEventHandler(SharpIdeFileGodotContainer file);
	
	[Export]
	public Texture2D CsharpFileIcon { get; set; } = null!;
	[Export]
	public Texture2D FolderIcon { get; set; } = null!;
	
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;
	private Tree _tree = null!;
	public override void _Ready()
	{
		_tree = GetNode<Tree>("Tree");
		_tree.ItemMouseSelected += TreeOnItemMouseSelected;
	}

	private void TreeOnItemMouseSelected(Vector2 mousePosition, long mouseButtonIndex)
	{
		var selected = _tree.GetSelected();
		if (selected is null) return;
		var sharpIdeFileContainer = selected.GetMetadata(0).As<SharpIdeFileGodotContainer?>();
		if (sharpIdeFileContainer is null) return;
		var sharpIdeFile = sharpIdeFileContainer.File;
		Guard.Against.Null(sharpIdeFile, nameof(sharpIdeFile));
		EmitSignalFileSelected(sharpIdeFileContainer);
	}

	public void RepopulateTree()
	{
		_tree.Clear();

		var rootItem = _tree.CreateItem();
		rootItem.SetText(0, SolutionModel.Name);

		// Add projects directly under solution
		foreach (var project in SolutionModel.Projects)
		{
			AddProjectToTree(rootItem, project);
		}

		// Add folders under solution
		foreach (var folder in SolutionModel.Folders)
		{
			AddSlnFolderToTree(rootItem, folder);
		}
	}

	private void AddSlnFolderToTree(TreeItem parent, SharpIdeSolutionFolder folder)
	{
		var folderItem = _tree.CreateItem(parent);
		folderItem.SetText(0, folder.Name);

		foreach (var project in folder.Projects)
		{
			AddProjectToTree(folderItem, project);
		}

		foreach (var subFolder in folder.Folders)
		{
			AddSlnFolderToTree(folderItem, subFolder); // recursion
		}

		foreach (var sharpIdeFile in folder.Files)
		{
			AddFileToTree(folderItem, sharpIdeFile);
		}
	}

	private void AddProjectToTree(TreeItem parent, SharpIdeProjectModel project)
	{
		var projectItem = _tree.CreateItem(parent);
		projectItem.SetText(0, project.Name);

		foreach (var sharpIdeFolder in project.Folders)
		{
			AddFoldertoTree(projectItem, sharpIdeFolder);
		}

		foreach (var file in project.Files)
		{
			AddFileToTree(projectItem, file);
		}
	}

	private void AddFoldertoTree(TreeItem projectItem, SharpIdeFolder sharpIdeFolder)
	{
		var folderItem = _tree.CreateItem(projectItem);
		folderItem.SetText(0, sharpIdeFolder.Name);
		folderItem.SetIcon(0, FolderIcon);

		foreach (var subFolder in sharpIdeFolder.Folders)
		{
			AddFoldertoTree(folderItem, subFolder); // recursion
		}

		foreach (var file in sharpIdeFolder.Files)
		{
			AddFileToTree(folderItem, file);
		}
	}

	private void AddFileToTree(TreeItem parent, SharpIdeFile file)
	{
		var fileItem = _tree.CreateItem(parent);
		fileItem.SetText(0, file.Name);
		fileItem.SetIcon(0, CsharpFileIcon);
		var container = new SharpIdeFileGodotContainer { File = file };
		// TODO: Handle ObjectDB instances leaked at exit
		fileItem.SetMetadata(0, container);
	}
	

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
}