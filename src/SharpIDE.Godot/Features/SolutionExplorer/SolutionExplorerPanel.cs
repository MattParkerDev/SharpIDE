using System.Collections.Specialized;
using Ardalis.GuardClauses;
using Godot;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Common;
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
	private TreeItem _rootItem = null!;
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
		
		var mouseButtonMask = (MouseButtonMask)mouseButtonIndex;

		var genericMetadata = selected.GetMetadata(0).As<RefCounted?>();
		switch (mouseButtonMask, genericMetadata)
		{
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeFile> fileContainer): GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(fileContainer.Item, null); break;
			case (MouseButtonMask.Right, RefCountedContainer<SharpIdeFile> fileContainer): OpenContextMenuFile(fileContainer.Item); break;
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeProjectModel>): break;
			case (MouseButtonMask.Right, RefCountedContainer<SharpIdeProjectModel> projectContainer): OpenContextMenuProject(projectContainer.Item); break;
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeFolder>): break;
			case (MouseButtonMask.Right, RefCountedContainer<SharpIdeFolder> folderContainer): OpenContextMenuFolder(folderContainer.Item); break;
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeSolutionFolder>): break;
			default: break;
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
		if (item.GetTypedMetadata<RefCountedContainer<SharpIdeFile>?>(0)?.Item == file)
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

	public void BindToSolution() => BindToSolution(SolutionModel);
	[RequiresGodotUiThread]
	public void BindToSolution(SharpIdeSolutionModel solution)
	{
	    _tree.Clear();

	    // Root
	    var rootItem = _tree.CreateItem();
	    rootItem.SetText(0, solution.Name);
	    rootItem.SetIcon(0, SlnIcon);
	    _rootItem = rootItem;

	    // Observe Projects
	    var projectsView = solution.Projects.CreateView(y => new TreeItemContainer());
		projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, rootItem, s.Value));
	        
	    projectsView.ObserveChanged()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
			    NotifyCollectionChangedAction.Add => this.InvokeAsync(() => e.NewItem.View.Value = CreateProjectTreeItem(_tree, _rootItem, e.NewItem.Value)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        })).AddTo(this);

	    // Observe Solution Folders
	    var foldersView = solution.SlnFolders
		    .WithInitialPopulation(s => CreateSlnFolderTreeItem(_tree, _rootItem, s))
		    .CreateView(y => new TreeItemContainer());
	    foldersView.ObserveChanged()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
	            NotifyCollectionChangedAction.Add => this.InvokeAsync(() => CreateSlnFolderTreeItem(_tree, _rootItem, e)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        })).AddTo(this);
	    
	    rootItem.SetCollapsedRecursive(true);
	    rootItem.Collapsed = false;
	}

	[RequiresGodotUiThread]
	private void CreateSlnFolderTreeItem(Tree tree, TreeItem parent, ViewChangedEvent<SharpIdeSolutionFolder, TreeItemContainer> e)
	{
	    var folderItem = tree.CreateItem(parent);
	        folderItem.SetText(0, e.NewItem.Value.Name);
	        folderItem.SetIcon(0, SlnFolderIcon);
	        folderItem.SetMetadata(0, new RefCountedContainer<SharpIdeSolutionFolder>(e.NewItem.Value));
	        e.NewItem.View.Value = folderItem;

	        // Observe folder sub-collections
	        var subFoldersView = e.NewItem.Value.Folders
		        .WithInitialPopulation(s => CreateSlnFolderTreeItem(_tree, folderItem, s))
		        .CreateView(y => new TreeItemContainer());
	        subFoldersView.ObserveChanged()
	            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
	            {
	                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => CreateSlnFolderTreeItem(_tree, folderItem, innerEvent)),
	                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
	                _ => Task.CompletedTask
	            })).AddTo(this);

	        var projectsView = e.NewItem.Value.Projects.CreateView(y => new TreeItemContainer());
	        projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, folderItem, s.Value));
	        projectsView.ObserveChanged()
	            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
	            {
	                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateProjectTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
	                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
	                _ => Task.CompletedTask
	            })).AddTo(this);

	        var filesView = e.NewItem.Value.Files
		        .WithInitialPopulation(s => CreateFileTreeItem(_tree, folderItem, s))
		        .CreateView(y => new TreeItemContainer());
	        filesView.ObserveChanged()
	            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
	            {
	                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => CreateFileTreeItem(_tree, folderItem, innerEvent)),
	                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
	                _ => Task.CompletedTask
	            })).AddTo(this);
	}

	[RequiresGodotUiThread]
	private TreeItem CreateProjectTreeItem(Tree tree, TreeItem parent, SharpIdeProjectModel projectModel)
	{
		var projectItem = tree.CreateItem(parent);
		projectItem.SetText(0, projectModel.Name);
		projectItem.SetIcon(0, CsprojIcon);
		projectItem.SetMetadata(0, new RefCountedContainer<SharpIdeProjectModel>(projectModel));

		// Observe project folders
		var foldersView = projectModel.Folders.CreateView(y => new TreeItemContainer());
		foldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFolderTreeItem(_tree, projectItem, s.Value));
		
		foldersView.ObserveChanged()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFolderTreeItem(_tree, projectItem, innerEvent.NewItem.Value)),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddTo(this);

		// Observe project files
		var filesView = projectModel.Files
			.WithInitialPopulation(s => CreateFileTreeItem(_tree, projectItem, s))
			.CreateView(y => new TreeItemContainer());
		filesView.ObserveChanged()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => CreateFileTreeItem(_tree, projectItem, innerEvent)),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddTo(this);
		return projectItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateFolderTreeItem(Tree tree, TreeItem parent, SharpIdeFolder sharpIdeFolder)
	{
		var folderItem = tree.CreateItem(parent);
		folderItem.SetText(0, sharpIdeFolder.Name);
		folderItem.SetIcon(0, FolderIcon);
		folderItem.SetMetadata(0, new RefCountedContainer<SharpIdeFolder>(sharpIdeFolder));
		
		// Observe subfolders
		var subFoldersView = sharpIdeFolder.Folders.CreateView(y => new TreeItemContainer());
		subFoldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFolderTreeItem(_tree, folderItem, s.Value));
		
		subFoldersView.ObserveChanged()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFolderTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddTo(this);

		// Observe files
		var filesView = sharpIdeFolder.Files
			.WithInitialPopulation(s => CreateFileTreeItem(_tree, folderItem, s))
			.CreateView(y => new TreeItemContainer());
		filesView.ObserveChanged()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => CreateFileTreeItem(_tree, folderItem, innerEvent)),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddTo(this);
		return folderItem;
	}

	[RequiresGodotUiThread]
	private void CreateFileTreeItem(Tree tree, TreeItem parent, ViewChangedEvent<SharpIdeFile, TreeItemContainer> e)
	{
		var fileItem = tree.CreateItem(parent);
		fileItem.SetText(0, e.NewItem.Value.Name);
		fileItem.SetIcon(0, CsharpFileIcon);
		fileItem.SetMetadata(0, new RefCountedContainer<SharpIdeFile>(e.NewItem.Value));
		e.NewItem.View.Value = fileItem;
	}

	private async Task FreeTreeItem(TreeItem? item)
	{
	    await this.InvokeAsync(() => item?.Free());
	}
}
