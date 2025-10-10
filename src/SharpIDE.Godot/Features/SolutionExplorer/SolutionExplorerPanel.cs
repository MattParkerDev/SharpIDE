using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel : MarginContainer
{
	[Export]
	public Texture2D CsharpFileIcon { get; set; } = null!;
	[Export]
	public Texture2D FolderIcon { get; set; } = null!;
	[Export]
	public Texture2D SlnFolderIcon { get; set; } = null!;
	[Export]
	public Texture2D CsprojIcon { get; set; } = null!;
	[Export]
	public Texture2D SlnIcon { get; set; } = null!;
	
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;
	private Tree _tree = null!;
	public override void _Ready()
	{
		_tree = GetNode<Tree>("Tree");
		_tree.ItemMouseSelected += TreeOnItemMouseSelected;
		GodotGlobalEvents.Instance.FileExternallySelected.Subscribe(OnFileExternallySelected);
	}

	private void TreeOnItemMouseSelected(Vector2 mousePosition, long mouseButtonIndex)
	{
		var selected = _tree.GetSelected();
		if (selected is null) return;
		var sharpIdeFileContainer = selected.GetMetadata(0).As<RefCountedContainer<SharpIdeFile>?>();
		if (sharpIdeFileContainer is null) return;
		var sharpIdeFile = sharpIdeFileContainer.Item;
		Guard.Against.Null(sharpIdeFile, nameof(sharpIdeFile));
		
		if (mouseButtonIndex is (int)MouseButtonMask.Left)
		{
			GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(sharpIdeFile, null);
		}
		else if (mouseButtonIndex is (int)MouseButtonMask.Right)
		{
			var menu = new PopupMenu();
			AddChild(menu);
			menu.AddItem("Open", 0);
			menu.AddItem("Reveal in File Explorer", 1);
			menu.AddSeparator();
			menu.AddItem("Copy Full Path", 2);
			menu.PopupHide += () =>
			{
				GD.Print("QueueFree menu");
				menu.QueueFree();
			};
			menu.IdPressed += id =>
			{
				if (id is 0)
				{
					GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(sharpIdeFile, null);
				}
				else if (id is 1)
				{
					OS.ShellOpen(Path.GetDirectoryName(sharpIdeFile.Path)!);
				}
				else if (id is 2)
				{
					DisplayServer.ClipboardSet(sharpIdeFile.Path);
				}
			};
			
			var globalMousePosition = GetGlobalMousePosition();
			menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
			menu.Popup();
		}
	}
	
	private async Task OnFileExternallySelected(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var task = GodotGlobalEvents.Instance.FileSelected.InvokeParallelAsync(file, fileLinePosition);
		var item = FindItemRecursive(_tree.GetRoot(), file);
		if (item is not null)
		{
			await this.InvokeAsync(() =>
			{
				item.UncollapseTree();
				_tree.SetSelected(item, 0);
				_tree.ScrollToItem(item, true);
				_tree.QueueRedraw();
			});
		}
		await task.ConfigureAwait(false);
	}
	
	private static TreeItem? FindItemRecursive(TreeItem item, SharpIdeFile file)
	{
		var metadata = item.GetMetadata(0);
		if (metadata.As<RefCountedContainer<SharpIdeFile>?>()?.Item == file)
			return item;

		var child = item.GetFirstChild();
		while (child != null)
		{
			var result = FindItemRecursive(child, file);
			if (result != null)
				return result;

			child = child.GetNext();
		}

		return null;
	}

	public void RepopulateTree()
	{
		_tree.Clear();

		var rootItem = _tree.CreateItem();
		rootItem.SetText(0, SolutionModel.Name);
		rootItem.SetIcon(0, SlnIcon);

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
		rootItem.SetCollapsedRecursive(true);
		rootItem.Collapsed = false;
	}

	private void AddSlnFolderToTree(TreeItem parent, SharpIdeSolutionFolder folder)
	{
		var folderItem = _tree.CreateItem(parent);
		folderItem.SetText(0, folder.Name);
		folderItem.SetIcon(0, SlnFolderIcon);

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
		projectItem.SetIcon(0, CsprojIcon);

		foreach (var sharpIdeFolder in project.Folders)
		{
			AddFolderToTree(projectItem, sharpIdeFolder);
		}

		foreach (var file in project.Files)
		{
			AddFileToTree(projectItem, file);
		}
	}

	private void AddFolderToTree(TreeItem projectItem, SharpIdeFolder sharpIdeFolder)
	{
		var folderItem = _tree.CreateItem(projectItem);
		folderItem.SetText(0, sharpIdeFolder.Name);
		folderItem.SetIcon(0, FolderIcon);

		foreach (var subFolder in sharpIdeFolder.Folders)
		{
			AddFolderToTree(folderItem, subFolder); // recursion
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
		var container = new RefCountedContainer<SharpIdeFile>(file);
		fileItem.SetMetadata(0, container);
	}
}
