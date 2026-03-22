using System.Collections.Specialized;
using Ardalis.GuardClauses;
using Godot;
using ObservableCollections;
using R3;
using SharpIDE.Application;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.BottomPanel;
using SharpIDE.Godot.Features.Common;
using SharpIDE.Godot.Features.Git;

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
	public Texture2D LoadingProjectIcon { get; set; } = null!;
	[Export]
	public Texture2D UnloadedProjectIcon { get; set; } = null!;
	[Export]
	public Texture2D SlnIcon { get; set; } = null!;
	[Export]
	public StyleBoxFlat SearchMatchHighlight { get; set; } = null!;
	
	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;
	private PanelContainer _panelContainer = null!;
	private Tree _tree = null!;
	private TreeItem _rootItem = null!;
	private LineEdit _searchInput = null!;
	
	private readonly Dictionary<TreeItem, bool> _treeItemCollapsedStates = [];

	private enum ClipboardOperation { Cut, Copy }

	private (List<IFileOrFolder>, ClipboardOperation)? _itemsOnClipboard;
	public override void _Ready()
	{
		_panelContainer = GetNode<PanelContainer>("%TreeContainer");
		_tree = GetNode<Tree>("%Tree");
		_tree.ItemMouseSelected += TreeOnItemMouseSelected;
		_searchInput = GetNode<LineEdit>("%SearchInput");
		_searchInput.TextChanged += OnSearchInputChanged;
		// Remove the tree from the scene tree for now, we will add it back when we bind to a solution
		_panelContainer.RemoveChild(_tree);
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
			HideSearch();
		}
		else if (@event.IsActionPressed(InputStringNames.FindInSolutionExplorer, exactMatch: true))
		{
			if (!IsSearchActive())
				ShowSearch();
			else
				HideSearch();
			
			AcceptEvent();
		}
	}

	private void OnSearchInputChanged(string newText)
	{
		if (!IsSearchActive() || string.IsNullOrWhiteSpace(newText))
		{
			RestoreTreeItemCollapsedStates(_rootItem);
			ShowEntireTree(_rootItem);
			ScrollToSelectedTreeItem();
		}
		else
		{
			FilterTree(_rootItem, newText);
		}
		
		_tree.QueueRedraw();
	}

	private static void ShowEntireTree(TreeItem item)
	{
		item.Visible = true;
		foreach (var child in item.GetChildren())
		{
			ShowEntireTree(child);
		}
	}

	private static bool FilterTree(TreeItem item, string searchText)
	{
		var itemText = item.GetText(0);
		var isMatch = itemText.Contains(searchText, StringComparison.OrdinalIgnoreCase);

		var hasMatchingChild = false;
		foreach (var child in item.GetChildren())
		{
			if (FilterTree(child, searchText))
				hasMatchingChild = true;
		}

		item.Visible = isMatch || hasMatchingChild;
		item.Collapsed = !hasMatchingChild;

		return isMatch || hasMatchingChild;
	}

	private bool IsSearchActive() => _searchInput.IsVisible();

	private void ShowSearch()
	{
		if (IsSearchActive()) return;
		
		_treeItemCollapsedStates.Clear();
		SaveTreeItemCollapsedStates(_rootItem);
		_searchInput.GrabFocus();
		_searchInput.Show();
		if (!string.IsNullOrWhiteSpace(_searchInput.Text))
			FilterTree(_rootItem, _searchInput.Text);
	}

	private void HideSearch()
	{
		if (!IsSearchActive()) return;
		
		_searchInput.Hide();
		RestoreTreeItemCollapsedStates(_rootItem);
		ShowEntireTree(_rootItem);
		ScrollToSelectedTreeItem();
	}

	private void SaveTreeItemCollapsedStates(TreeItem item)
	{
		_treeItemCollapsedStates[item] = item.Collapsed;

		foreach (var child in item.GetChildren())
		{
			SaveTreeItemCollapsedStates(child);
		}
	}

	private void RestoreTreeItemCollapsedStates(TreeItem item)
	{
		// If an item was selected during the search then we want to keep it uncollapsed, otherwise we restore it to the state before the search.
		item.Collapsed = !HasSelectedChild(item) && _treeItemCollapsedStates.TryGetValue(item, out var collapsed) && collapsed;

		foreach (var child in item.GetChildren())
		{
			RestoreTreeItemCollapsedStates(child);
		}
	}

	private static bool HasSelectedChild(TreeItem item) => item.GetChildren().Any(child => child.IsSelected(0) || HasSelectedChild(child));

	private void ScrollToSelectedTreeItem()
	{
		if (_tree.GetSelected() is not { } selected) return;
		
		_tree.ScrollToItem(selected, centerOnItem: true);
	}

	private void TreeOnItemMouseSelected(Vector2 mousePosition, long mouseButtonIndex)
	{
		var selected = _tree.GetSelected();
		if (selected is null) return;
		if (HasMultipleNodesSelected()) return;
		
		var mouseButtonMask = (MouseButtonMask)mouseButtonIndex;

		var sharpIdeNode = selected.SharpIdeNode;
		switch (mouseButtonMask, sharpIdeNode)
		{
			case (MouseButtonMask.Left, SharpIdeFile file): GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(file, null); break;
			case (MouseButtonMask.Right, SharpIdeFile file): OpenContextMenuFile(file); break;
			case (MouseButtonMask.Left, SharpIdeProjectModel { IsInvalid: true }): GodotGlobalEvents.Instance.BottomPanelTabExternallySelected.InvokeParallelFireAndForget(BottomPanelType.Problems); break;
			case (MouseButtonMask.Right, SharpIdeProjectModel project): OpenContextMenuProject(project); break;
			case (MouseButtonMask.Left, SharpIdeFolder): break;
			case (MouseButtonMask.Right, SharpIdeFolder folder): OpenContextMenuFolder(folder, selected); break;
			case (MouseButtonMask.Left, SharpIdeSolutionFolder): break;
			default: break;
		}
	}
	
	private async Task OnFileExternallySelected(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var task = GodotGlobalEvents.Instance.FileSelected.InvokeParallelAsync(file, fileLinePosition);
		if (file.IsMetadataAsSourceFile)
		{
			await task;
			return;
		}
		// First check if the file is already selected
		var selectedItem = _tree.GetSelected();
		if (selectedItem is not null)
		{
			var selectedSharpIdeNode = selectedItem.SharpIdeNode;
			if (selectedSharpIdeNode == file)
				return;
		}
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
		if (item.SharpIdeNode == file)
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

	public async Task BindToSolution() => await BindToSolution(SolutionModel);
	[RequiresGodotUiThread]
	public async Task BindToSolution(SharpIdeSolutionModel solution)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SolutionExplorerPanel)}.{nameof(BindToSolution)}");
		
		// Solutions with hundreds of thousands of files can cause the ui to freeze as the tree is populated
		// the Tree has been removed from the scene tree in _Ready, so we can operate on it off the ui thread, then add it back
		_tree.Clear();

	    // Root
	    var rootItem = CreateTreeItem();
	    rootItem.SetText(0, solution.Name);
	    rootItem.SetIcon(0, SlnIcon);
	    _rootItem = rootItem;

	    // Observe Projects
	    var projectsView = solution.Projects.CreateView(y => new TreeItemContainer());
		projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, rootItem, s.Value));
	    projectsView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
			    NotifyCollectionChangedAction.Add => this.InvokeAsync(() => e.NewItem.View.Value = CreateProjectTreeItem(_tree, _rootItem, e.NewItem.Value)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        })).AddToDeferred(this);

	    // Observe Solution Folders
	    var foldersView = solution.SlnFolders.CreateView(y => new TreeItemContainer());
	    foldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSlnFolderTreeItem(_tree, rootItem, s.Value));
	    foldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
	            NotifyCollectionChangedAction.Add => this.InvokeAsync(() => e.NewItem.View.Value = CreateSlnFolderTreeItem(_tree, _rootItem, e.NewItem.Value)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        })).AddToDeferred(this);
	    
	    rootItem.SetCollapsedRecursive(true);
	    rootItem.Collapsed = false;
	    await this.InvokeAsync(() =>
	    {
		    _panelContainer.AddChild(_tree);
	    });
	}

	private TreeItem CreateTreeItem(TreeItem? parent = null, int index = -1)
	{
		var item = _tree.CreateItem(parent, index);
		item.SetCellMode(0, TreeItem.TreeCellMode.Custom);
		item.SetCustomDrawCallback(0, Callable.From<TreeItem, Rect2>(TreeItemCustomDraw));
		return item;
	}
	
	private void TreeItemCustomDraw(TreeItem item, Rect2 rect)
	{
		if (!IsSearchActive() || string.IsNullOrWhiteSpace(_searchInput.Text)) return;
		
		var text = item.GetText(0);
		var matchIndex = text.FindN(_searchInput.Text);

		if (matchIndex < 0) return;

		var icon = item.GetIcon(0);
		var font = _tree.GetThemeFont(ThemeStringNames.Font);
		var fontSize = _tree.GetThemeFontSize(ThemeStringNames.FontSize);
		var separation = _tree.GetThemeConstant(ThemeStringNames.HSeparation);
		var textMatchX = separation + font.GetStringSize(text.Left(matchIndex), HorizontalAlignment.Left, width: -1f, fontSize).X;
		var highlightPosition = new Vector2(rect.Position.X + textMatchX + (icon?.GetWidth() ?? 0), rect.Position.Y);
		var highlightSize = new Vector2(font.GetStringSize(_searchInput.Text, HorizontalAlignment.Left, width: -1f, fontSize).X, rect.Size.Y);
		
		var highlightRect = new Rect2(highlightPosition, highlightSize);
		_tree.DrawStyleBox(SearchMatchHighlight, highlightRect);
	}

	[RequiresGodotUiThread]
	private TreeItem CreateSlnFolderTreeItem(Tree tree, TreeItem parent, SharpIdeSolutionFolder slnFolder)
	{
	    var folderItem = CreateTreeItem(parent);
        folderItem.SetText(0, slnFolder.Name);
        folderItem.SetIcon(0, SlnFolderIcon);
        folderItem.SharpIdeNode = slnFolder;

        // Observe folder sub-collections
        var subFoldersView = slnFolder.Folders.CreateView(y => new TreeItemContainer());
        subFoldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSlnFolderTreeItem(_tree, folderItem, s.Value));
        
        subFoldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
            {
                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateSlnFolderTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
                _ => Task.CompletedTask
            })).AddToDeferred(this);

        var projectsView = slnFolder.Projects.CreateView(y => new TreeItemContainer());
        projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, folderItem, s.Value));
        projectsView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
            {
                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateProjectTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
                _ => Task.CompletedTask
            })).AddToDeferred(this);

        var filesView = slnFolder.Files.CreateView(y => new TreeItemContainer());
        filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, folderItem, s.Value));
        filesView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
            {
                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, folderItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
                _ => Task.CompletedTask
            })).AddToDeferred(this);
        return folderItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateProjectTreeItem(Tree tree, TreeItem parent, SharpIdeProjectModel projectModel)
	{
		var projectItem = CreateTreeItem(parent);
		projectItem.SetText(0, projectModel.Name.Value);
		var icon = projectModel.IsLoading ? LoadingProjectIcon : projectModel.IsInvalid ? UnloadedProjectIcon : CsprojIcon;
		projectItem.SetIcon(0, icon);
		if (projectModel.IsLoading is false && projectModel.IsInvalid) projectItem.SetSuffix(0, " ·  load failed");
		projectItem.SharpIdeNode = projectModel;
		
		projectModel.MsBuildProjectLoadState.SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (loadState, ct) =>
		{
			var newIcon = loadState switch
			{
				MsBuildProjectLoadState.Loading => LoadingProjectIcon,
				MsBuildProjectLoadState.Loaded => CsprojIcon,
				MsBuildProjectLoadState.Invalid => UnloadedProjectIcon,
				MsBuildProjectLoadState.Unloaded => UnloadedProjectIcon,
				_ => throw new ArgumentOutOfRangeException(nameof(loadState), loadState, null)
			};
			var suffix = loadState is MsBuildProjectLoadState.Invalid ? " ·  load failed" : string.Empty;
			await this.InvokeAsync(() =>
			{
				projectItem.SetIcon(0, newIcon);
				projectItem.SetSuffix(0, suffix);
			});
		}).AddToDeferred(this);

		// Observe project folders
		var foldersView = projectModel.Folders.CreateView(y => new TreeItemContainer());
		foldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFolderTreeItem(_tree, projectItem, s.Value));
		
		foldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFolderTreeItem(_tree, projectItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
				NotifyCollectionChangedAction.Move => MoveTreeItem(_tree, innerEvent.NewItem.View, innerEvent.NewItem.Value, innerEvent.OldStartingIndex, innerEvent.NewStartingIndex),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddToDeferred(this);
		
		// Observe project files
		var filesView = projectModel.Files.CreateView(y => new TreeItemContainer());
		filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, projectItem, s.Value));
		filesView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, projectItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
				NotifyCollectionChangedAction.Move => MoveTreeItem(_tree, innerEvent.NewItem.View, innerEvent.NewItem.Value, innerEvent.OldStartingIndex, innerEvent.NewStartingIndex),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddToDeferred(this);
		return projectItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateFolderTreeItem(Tree tree, TreeItem parent, SharpIdeFolder sharpIdeFolder, int newStartingIndex = -1)
	{
		var folderItem = CreateTreeItem(parent, newStartingIndex);
		folderItem.SetText(0, sharpIdeFolder.Name.Value);
		folderItem.SetIcon(0, FolderIcon);
		folderItem.SharpIdeNode = sharpIdeFolder;
		
		Observable.EveryValueChanged(sharpIdeFolder, folder => folder.Name.Value)
			.Skip(1).SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (s, ct) =>
			{
				await this.InvokeAsync(() => folderItem.SetText(0, s));
			}).AddToDeferred(this);
		
		// Observe subfolders
		var subFoldersView = sharpIdeFolder.Folders.CreateView(y => new TreeItemContainer());
		subFoldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFolderTreeItem(_tree, folderItem, s.Value));
		
		subFoldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFolderTreeItem(_tree, folderItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
				NotifyCollectionChangedAction.Move => MoveTreeItem(_tree, innerEvent.NewItem.View, innerEvent.NewItem.Value, innerEvent.OldStartingIndex, innerEvent.NewStartingIndex),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddToDeferred(this);

		// Observe files
		var filesView = sharpIdeFolder.Files.CreateView(y => new TreeItemContainer());
		filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, folderItem, s.Value));
		filesView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
			{
				NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, folderItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
				NotifyCollectionChangedAction.Move => MoveTreeItem(_tree, innerEvent.NewItem.View, innerEvent.NewItem.Value, innerEvent.OldStartingIndex, innerEvent.NewStartingIndex),
				NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
				_ => Task.CompletedTask
			})).AddToDeferred(this);
		return folderItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateFileTreeItem(Tree tree, TreeItem parent, SharpIdeFile sharpIdeFile, int newStartingIndex = -1)
	{
		// We need to offset the starting index by the number of non-file items (folders/projects) in the parent
		// because the newStartingIndex is calculated based on all children, but we are only inserting files here
		if (newStartingIndex >= 0)
		{
			var sharpIdeParent = sharpIdeFile.Parent as IFolderOrProject;
			Guard.Against.Null(sharpIdeParent, nameof(sharpIdeParent));
			var folderCount = sharpIdeParent.Folders.Count;
			newStartingIndex += folderCount;
		}
		var fileItem = CreateTreeItem(parent, newStartingIndex);
		fileItem.SetText(0, sharpIdeFile.Name.Value);
		fileItem.SetIconsForFileExtension(sharpIdeFile);
		if (GitColours.GetColorForGitFileStatus(sharpIdeFile.GitStatus) is { } notnullColor) fileItem.SetCustomColor(0, notnullColor);
		else fileItem.ClearCustomColor(0);
		fileItem.SharpIdeNode = sharpIdeFile;
		
		Observable.EveryValueChanged(sharpIdeFile, file => file.Name.Value)
			.Skip(1).SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (s, ct) =>
			{
				await this.InvokeAsync(() =>
				{
					fileItem.SetText(0, s);
					fileItem.SetIconsForFileExtension(sharpIdeFile);
				});
			}).AddToDeferred(this);
		
		return fileItem;
	}
	
	private async Task MoveTreeItem(Tree tree, TreeItemContainer treeItemContainer, IFileOrFolder fileOrFolder, int oldStartingIndex, int newStartingIndex)
	{
		if (oldStartingIndex == newStartingIndex) throw new InvalidOperationException("Old and new starting indexes are the same");
		var treeItem = treeItemContainer.Value!;
		var isFile = fileOrFolder is SharpIdeFile;
		if (isFile)
		{
			var sharpIdeParent = fileOrFolder.Parent as IFolderOrProject;
			Guard.Against.Null(sharpIdeParent, nameof(sharpIdeParent));
			var folderCount = sharpIdeParent.Folders.Count;
			newStartingIndex += folderCount;
		}
		
		await this.InvokeAsync(() =>
		{
			treeItem.MoveToIndexInParent(oldStartingIndex, newStartingIndex);
		});
	}

	private async Task FreeTreeItem(TreeItem? item)
	{
	    await this.InvokeAsync(() => item?.Free());
	}
}
