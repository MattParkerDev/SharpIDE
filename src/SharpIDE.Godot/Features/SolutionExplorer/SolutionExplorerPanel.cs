using System.Collections.Specialized;
using Ardalis.GuardClauses;
using Godot;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.NavigationHistory;
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
	
	[Inject] private readonly IdeNavigationHistoryService _navigationHistoryService = null!;
	private enum ClipboardOperation { Cut, Copy }

	private (List<IFileOrFolder>, ClipboardOperation)? _itemsOnClipboard;
	public override void _Ready()
	{
		_tree = GetNode<Tree>("Tree");
		_tree.ItemMouseSelected += TreeOnItemMouseSelected;
		GodotGlobalEvents.Instance.FileExternallySelected.Subscribe(OnFileExternallySelected);
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		// Copy
		if (@event is InputEventKey { Pressed: true, Keycode: Key.C, CtrlPressed: true })
		{
			CopySelectedNodesToSlnExplorerClipboard();
		}
		// Cut
		else if (@event is InputEventKey { Pressed: true, Keycode: Key.X, CtrlPressed: true })
		{
			CutSelectedNodeToSlnExplorerClipboard();
		}
		// Paste
		else if (@event is InputEventKey { Pressed: true, Keycode: Key.V, CtrlPressed: true })
		{
			CopyNodesFromClipboardToSelectedNode();
		}
		else if (@event is InputEventKey { Pressed: true, Keycode: Key.Delete })
		{
			// TODO: DeleteSelectedNodes();
		}
		else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			ClearSlnExplorerClipboard();
		}
	}

	private void TreeOnItemMouseSelected(Vector2 mousePosition, long mouseButtonIndex)
	{
		var selected = _tree.GetSelected();
		if (selected is null) return;
		if (HasMultipleNodesSelected()) return;
		
		var mouseButtonMask = (MouseButtonMask)mouseButtonIndex;

		var genericMetadata = selected.GetMetadata(0).As<RefCounted?>();
		switch (mouseButtonMask, genericMetadata)
		{
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeFile> fileContainer): GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(fileContainer.Item, null); break;
			case (MouseButtonMask.Right, RefCountedContainer<SharpIdeFile> fileContainer): OpenContextMenuFile(fileContainer.Item); break;
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeProjectModel>): break;
			case (MouseButtonMask.Right, RefCountedContainer<SharpIdeProjectModel> projectContainer): OpenContextMenuProject(projectContainer.Item); break;
			case (MouseButtonMask.Left, RefCountedContainer<SharpIdeFolder>): break;
			case (MouseButtonMask.Right, RefCountedContainer<SharpIdeFolder> folderContainer): OpenContextMenuFolder(folderContainer.Item, selected); break;
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
		_navigationHistoryService.RecordNavigation(file, fileLinePosition ?? new SharpIdeFileLinePosition(0, 0));
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
	    var foldersView = solution.SlnFolders.CreateView(y => new TreeItemContainer());
	    foldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSlnFolderTreeItem(_tree, rootItem, s.Value));
	    foldersView.ObserveChanged()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
	            NotifyCollectionChangedAction.Add => this.InvokeAsync(() => e.NewItem.View.Value = CreateSlnFolderTreeItem(_tree, _rootItem, e.NewItem.Value)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        })).AddTo(this);
	    
	    rootItem.SetCollapsedRecursive(true);
	    rootItem.Collapsed = false;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateSlnFolderTreeItem(Tree tree, TreeItem parent, SharpIdeSolutionFolder slnFolder)
	{
	    var folderItem = tree.CreateItem(parent);
	        folderItem.SetText(0, slnFolder.Name);
	        folderItem.SetIcon(0, SlnFolderIcon);
	        folderItem.SetMetadata(0, new RefCountedContainer<SharpIdeSolutionFolder>(slnFolder));

	        // Observe folder sub-collections
	        var subFoldersView = slnFolder.Folders.CreateView(y => new TreeItemContainer());
	        subFoldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSlnFolderTreeItem(_tree, folderItem, s.Value));
	        
	        subFoldersView.ObserveChanged()
	            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
	            {
	                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateSlnFolderTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
	                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
	                _ => Task.CompletedTask
	            })).AddTo(this);

	        var projectsView = slnFolder.Projects.CreateView(y => new TreeItemContainer());
	        projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, folderItem, s.Value));
	        projectsView.ObserveChanged()
	            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
	            {
	                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateProjectTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
	                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
	                _ => Task.CompletedTask
	            })).AddTo(this);

	        var filesView = slnFolder.Files.CreateView(y => new TreeItemContainer());
	        filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, folderItem, s.Value));
	        filesView.ObserveChanged()
	            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
	            {
	                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
	                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
	                _ => Task.CompletedTask
	            })).AddTo(this);
	        return folderItem;
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
		var filesView = projectModel.Files.CreateView(y => new TreeItemContainer());
		filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, projectItem, s.Value));
		filesView.ObserveChanged()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, projectItem, innerEvent.NewItem.Value)),
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
		
		Observable.EveryValueChanged(sharpIdeFolder, folder => folder.Name)
			.Skip(1).SubscribeAwait(async (s, ct) =>
			{
				await this.InvokeAsync(() => folderItem.SetText(0, s));
			}).AddTo(this);
		
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
		var filesView = sharpIdeFolder.Files.CreateView(y => new TreeItemContainer());
		filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, folderItem, s.Value));
		filesView.ObserveChanged()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddTo(this);
		return folderItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateFileTreeItem(Tree tree, TreeItem parent, SharpIdeFile sharpIdeFile)
	{
		var fileItem = tree.CreateItem(parent);
		fileItem.SetText(0, sharpIdeFile.Name);
		fileItem.SetIcon(0, CsharpFileIcon);
		fileItem.SetMetadata(0, new RefCountedContainer<SharpIdeFile>(sharpIdeFile));
		
		Observable.EveryValueChanged(sharpIdeFile, folder => folder.Name)
			.Skip(1).SubscribeAwait(async (s, ct) =>
			{
				await this.InvokeAsync(() => fileItem.SetText(0, s));
			}).AddTo(this);
		
		return fileItem;
	}

	private async Task FreeTreeItem(TreeItem? item)
	{
	    await this.InvokeAsync(() => item?.Free());
	}
}
