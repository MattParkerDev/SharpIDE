using System.Collections.Specialized;
using Ardalis.GuardClauses;
using Godot;
using ObservableCollections;
using R3;
using SharpIDE.Application;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.Common;
using SharpIDE.Godot.Features.Git;
using SharpIDE.Godot.Features.ToolPanes;

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

	private Tree _tree = null!;
	private TreeItem _rootItem = null!;

	[Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;

	private enum ClipboardOperation { Cut, Copy }

	private (List<IFileOrFolder>, ClipboardOperation)? _itemsOnClipboard;
	public override void _Ready()
	{
		_tree = GetNode<Tree>("%Tree");
		_tree.ItemMouseSelected += TreeOnItemMouseSelected;
		// Remove the tree from the scene tree for now, we will add it back when we bind to a solution
		RemoveChild(_tree);
		GodotGlobalEvents.Instance.FileExternallySelected.Subscribe(OnFileExternallySelected);
		_ = Task.GodotRun(BindToSolution);
	}


	public override void _ShortcutInput(InputEvent @event)
	{
		if (@event.IsActionPressed(InputStringNames.RenameSymbol) && this.HasFocusOrIsParentOfFocus())
		{
			AcceptEvent();
			var selectedTreeItem = _tree.GetSelected();
			if (selectedTreeItem.SharpIdeNode is SharpIdeFile file)
			{
				OpenRenameFilePopup(file);
			}
			else if (selectedTreeItem.SharpIdeNode is SharpIdeFolder folder)
			{
				OpenRenameFolderPopup(folder);
			}
		}
		if (@event.IsActionPressed(InputStringNames.Delete) && this.HasFocusOrIsParentOfFocus())
		{
			AcceptEvent();
			var selectedTreeItem = _tree.GetSelected();
			if (selectedTreeItem.SharpIdeNode is SharpIdeFile file)
			{
				OpenDeleteFileConfirmation(file);
			}
			else if (selectedTreeItem.SharpIdeNode is SharpIdeFolder folder)
			{
				OpenDeleteFolderConfirmation(folder);
			}
		}
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
		if (_tree.HasMultipleNodesSelected()) return;

		var mouseButtonMask = (MouseButtonMask)mouseButtonIndex;

		var sharpIdeNode = selected.SharpIdeNode;
		switch (mouseButtonMask, sharpIdeNode)
		{
			case (MouseButtonMask.Left, SharpIdeFile file): GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(file, null); break;
			case (MouseButtonMask.Right, SharpIdeFile file): OpenContextMenuFile(file); break;
			case (MouseButtonMask.Left, SharpIdeSolutionFile { File: not null } slnFile): GodotGlobalEvents.Instance.FileSelected.InvokeParallelFireAndForget(slnFile.File, null); break;
			case (MouseButtonMask.Right, SharpIdeSolutionFile { File: not null } slnFile): OpenContextMenuFile(slnFile.File); break;
			case (MouseButtonMask.Left, SharpIdeProjectModel { IsInvalid: true }): GodotGlobalEvents.Instance.ToolPaneExternallyActivated.InvokeParallelFireAndForget(ToolPaneType.Problems); break;
			case (MouseButtonMask.Right, SharpIdeProjectModel { Folder: not null } project): OpenContextMenuProject(project); break;
			case (MouseButtonMask.Left, SharpIdeFolder): break;
			case (MouseButtonMask.Right, SharpIdeFolder folder): OpenContextMenuFolder(folder, selected); break;
			case (MouseButtonMask.Left, SharpIdeSolutionFolder): break;
			case (MouseButtonMask.Right, SharpIdeSolutionFolder slnFolder): OpenContextMenuSlnFolder(slnFolder, selected); break;
			case (MouseButtonMask.Left, SharpIdeSolutionModel): break;
			case (MouseButtonMask.Right, SharpIdeSolutionModel solution): OpenContextMenuSolution(solution, selected); break;
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
		var item = _rootItem is null ? null : FindItemRecursive(_rootItem, file);
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

	public async Task BindToSolution()
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(SolutionExplorerPanel)}.{nameof(BindToSolution)}");

		await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
		var solution = _sharpIdeSolutionAccessor.SolutionModel;

		// Solutions with hundreds of thousands of files can cause the ui to freeze as the tree is populated
		// the Tree has been removed from the scene tree in _Ready, so we can operate on it off the ui thread, then add it back
		_tree.Clear();

	    // Root
	    var rootItem = _tree.CreateItem();
	    rootItem.SetText(0, solution.Name);
	    rootItem.SetIcon(0, SlnIcon);
	    rootItem.SharpIdeNode = solution;
	    _rootItem = rootItem;

	    var disposableBuilder = new DisposableBuilder();

	    // Observe Projects
	    var projectsView = solution.Projects.CreateView(y => new TreeItemContainer());
		projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, rootItem, s.Value));
	    projectsView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
			    NotifyCollectionChangedAction.Add => this.InvokeAsync(() => e.NewItem.View.Value = CreateProjectTreeItem(_tree, _rootItem, e.NewItem.Value, e.NewStartingIndex)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        }), configureAwait: false).AddTo(ref disposableBuilder);

	    // Observe Solution Folders
	    var foldersView = solution.SlnFolders.CreateView(y => new TreeItemContainer());
	    foldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSlnFolderTreeItem(_tree, rootItem, s.Value));
	    foldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
	        .SubscribeAwait(async (e, ct) => await (e.Action switch
	        {
	            NotifyCollectionChangedAction.Add => this.InvokeAsync(() => e.NewItem.View.Value = CreateSlnFolderTreeItem(_tree, _rootItem, e.NewItem.Value)),
	            NotifyCollectionChangedAction.Remove => FreeTreeItem(e.OldItem.View.Value),
	            _ => Task.CompletedTask
	        }), configureAwait: false).AddTo(ref disposableBuilder);

	    rootItem.SetCollapsedRecursive(true);
	    rootItem.Collapsed = false;
	    rootItem.SharpIdeDisposable = disposableBuilder.Build();
	    await this.InvokeAsync(() =>
	    {
		    AddChild(_tree);
	    });
	}

	[RequiresGodotUiThread]
	private TreeItem CreateSlnFolderTreeItem(Tree tree, TreeItem parent, SharpIdeSolutionFolder slnFolder)
	{
	    var folderItem = tree.CreateItem(parent);
        folderItem.SetText(0, slnFolder.Name);
        folderItem.SetIcon(0, SlnFolderIcon);
        folderItem.SharpIdeNode = slnFolder;

        var disposableBuilder = new DisposableBuilder();

        // Observe folder sub-collections
        var subFoldersView = slnFolder.Folders.CreateView(y => new TreeItemContainer());
        subFoldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSlnFolderTreeItem(_tree, folderItem, s.Value));

        subFoldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
            {
                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateSlnFolderTreeItem(_tree, folderItem, innerEvent.NewItem.Value)),
                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
                _ => Task.CompletedTask
            }), configureAwait: false).AddTo(ref disposableBuilder);

        var projectsView = slnFolder.Projects.CreateView(y => new TreeItemContainer());
        projectsView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateProjectTreeItem(_tree, folderItem, s.Value));
        projectsView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
            {
                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateProjectTreeItem(_tree, folderItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
                _ => Task.CompletedTask
            }), configureAwait: false).AddTo(ref disposableBuilder);

        var filesView = slnFolder.Files.CreateView(y => new TreeItemContainer());
        filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateSolutionFileTreeItem(_tree, folderItem, s.Value));
        filesView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
            {
                NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateSolutionFileTreeItem(_tree, folderItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
                NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
                _ => Task.CompletedTask
            }), configureAwait: false).AddTo(ref disposableBuilder);
        folderItem.SharpIdeDisposable = disposableBuilder.Build();
        return folderItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateProjectTreeItem(Tree tree, TreeItem parent, SharpIdeProjectModel projectModel, int newStartingIndex = -1)
	{
		// We need to offset the starting index by the number of solution folders in the parent
		// because the newStartingIndex is calculated based on all children, but we are only inserting projects here
		if (newStartingIndex >= 0)
		{
			var siblingSlnFolders = parent.SharpIdeNode switch
			{
				SharpIdeSolutionModel s => s.SlnFolders,
				SharpIdeSolutionFolder s => s.Folders,
				_ => throw new InvalidOperationException("Project parent must be either solution or solution folder")
			};
			var folderCount = siblingSlnFolders.Count;
			newStartingIndex += folderCount;
		}
		var projectItem = tree.CreateItem(parent, newStartingIndex);
		projectItem.Collapsed = true;
		projectItem.SetText(0, projectModel.Name.Value);
		var icon = projectModel.IsLoading ? LoadingProjectIcon : projectModel.IsInvalid ? UnloadedProjectIcon : CsprojIcon;
		projectItem.SetIcon(0, icon);
		if (projectModel.IsLoading is false && projectModel.IsInvalid) projectItem.SetSuffix(0, " ·  load failed");
		projectItem.SharpIdeNode = projectModel;

        var disposableBuilder = new DisposableBuilder();

		projectModel.ActiveMsBuildProjectLoadState.SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (loadState, ct) =>
		{
			var newIcon = loadState switch
			{
				MsBuildProjectLoadState.Loading => LoadingProjectIcon,
				MsBuildProjectLoadState.Loaded => CsprojIcon,
				MsBuildProjectLoadState.Invalid => UnloadedProjectIcon,
				MsBuildProjectLoadState.Missing => UnloadedProjectIcon,
				MsBuildProjectLoadState.Unloaded => UnloadedProjectIcon,
				_ => throw new ArgumentOutOfRangeException(nameof(loadState), loadState, null)
			};
			var suffix = loadState is MsBuildProjectLoadState.Invalid ? " ·  load failed" : string.Empty;
			await this.InvokeAsync(() =>
			{
				projectItem.SetIcon(0, newIcon);
				projectItem.SetSuffix(0, suffix);
			});
		}, configureAwait: false).AddTo(ref disposableBuilder);

		// Observe project folder's subfolders and files
		var projectFolder = projectModel.Folder;

		if (projectFolder is not null)
		{
			var foldersView = projectFolder.Folders.CreateView(y => new TreeItemContainer());
			foldersView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFolderTreeItem(_tree, projectItem, s.Value));

			foldersView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
				{
					NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFolderTreeItem(_tree, projectItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
					NotifyCollectionChangedAction.Move => MoveTreeItem(_tree, innerEvent.NewItem.View, innerEvent.NewItem.Value, innerEvent.OldStartingIndex, innerEvent.NewStartingIndex),
					NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
					_ => Task.CompletedTask
				}), configureAwait: false).AddTo(ref disposableBuilder);

			var filesView = projectFolder.Files.CreateView(y => new TreeItemContainer());
			filesView.Unfiltered.ToList().ForEach(s => s.View.Value = CreateFileTreeItem(_tree, projectItem, s.Value));
			filesView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (innerEvent, ct) => await (innerEvent.Action switch
				{
					NotifyCollectionChangedAction.Add => this.InvokeAsync(() => innerEvent.NewItem.View.Value = CreateFileTreeItem(_tree, projectItem, innerEvent.NewItem.Value, innerEvent.NewStartingIndex)),
					NotifyCollectionChangedAction.Move => MoveTreeItem(_tree, innerEvent.NewItem.View, innerEvent.NewItem.Value, innerEvent.OldStartingIndex, innerEvent.NewStartingIndex),
					NotifyCollectionChangedAction.Remove => FreeTreeItem(innerEvent.OldItem.View.Value),
					_ => Task.CompletedTask
				}), configureAwait: false).AddTo(ref disposableBuilder);
		}

		projectItem.SharpIdeDisposable = disposableBuilder.Build();
		return projectItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateFolderTreeItem(Tree tree, TreeItem parent, SharpIdeFolder sharpIdeFolder, int newStartingIndex = -1)
	{
		var folderItem = tree.CreateItem(parent, newStartingIndex);
		folderItem.SetText(0, sharpIdeFolder.Name.Value);
		folderItem.SetIcon(0, FolderIcon);
		folderItem.SharpIdeNode = sharpIdeFolder;

		var disposableBuilder = Disposable.CreateBuilder();
		sharpIdeFolder.Name.Skip(1).SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (s, ct) =>
			{
				await this.InvokeAsync(() => folderItem.SetText(0, s));
			}, configureAwait: false).AddTo(ref disposableBuilder);

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
			}), configureAwait: false).AddTo(ref disposableBuilder);

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
			}), configureAwait: false).AddTo(ref disposableBuilder);
		folderItem.SharpIdeDisposable = disposableBuilder.Build();
		return folderItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateSolutionFileTreeItem(Tree tree, TreeItem parent, SharpIdeSolutionFile sharpIdeSolutionFile, int newStartingIndex = -1)
	{
		// We need to offset the starting index by the number of non-file items (folders/projects) in the parent
		// because the newStartingIndex is calculated based on all children, but we are only inserting files here
		if (newStartingIndex >= 0)
		{
			var sharpIdeParent = sharpIdeSolutionFile.Parent as SharpIdeSolutionFolder;
			Guard.Against.Null(sharpIdeParent, nameof(sharpIdeParent));
			var folderCount = sharpIdeParent.Folders.Count;
			var projectsCount = sharpIdeParent.Projects.Count;
			newStartingIndex += folderCount + projectsCount;
		}
		var fileItem = tree.CreateItem(parent, newStartingIndex);
		fileItem.SetText(0, sharpIdeSolutionFile.Name);
		if (sharpIdeSolutionFile.File is {} file)
		{
			fileItem.SetIconsForFileExtension(file);
			if (GitColours.GetColorForGitFileStatus(file.GitStatus) is { } notnullColor) fileItem.SetCustomColor(0, notnullColor);
			else fileItem.ClearCustomColor(0);
		}
		else
		{
			fileItem.SetWarningIcon();
			fileItem.ClearCustomColor(0);
		}

		fileItem.SharpIdeNode = sharpIdeSolutionFile;

		return fileItem;
	}

	[RequiresGodotUiThread]
	private TreeItem CreateFileTreeItem(Tree tree, TreeItem parent, SharpIdeFile sharpIdeFile, int newStartingIndex = -1)
	{
		// We need to offset the starting index by the number of non-file items (folders/projects) in the parent
		// because the newStartingIndex is calculated based on all children, but we are only inserting files here
		if (newStartingIndex >= 0)
		{
			var sharpIdeParent = sharpIdeFile.Parent as SharpIdeFolder;
			Guard.Against.Null(sharpIdeParent, nameof(sharpIdeParent));
			var folderCount = sharpIdeParent.Folders.Count;
			newStartingIndex += folderCount;
		}
		var disposableBuilder = Disposable.CreateBuilder();

		var fileItem = tree.CreateItem(parent, newStartingIndex);
		fileItem.SetText(0, sharpIdeFile.Name.Value);
		fileItem.SetIconsForFileExtension(sharpIdeFile);
		if (GitColours.GetColorForGitFileStatus(sharpIdeFile.GitStatus) is { } notnullColor) fileItem.SetCustomColor(0, notnullColor);
		else fileItem.ClearCustomColor(0);
		fileItem.SharpIdeNode = sharpIdeFile;

		sharpIdeFile.Name.Skip(1).SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (newName, ct) => await this.InvokeAsync(() =>
			{
				fileItem.SetText(0, newName);
				fileItem.SetIconsForFileExtension(sharpIdeFile);
			}), configureAwait: false)
			.AddTo(ref disposableBuilder);
		fileItem.SharpIdeDisposable = disposableBuilder.Build();
		return fileItem;
	}

	private async Task MoveTreeItem(Tree tree, TreeItemContainer treeItemContainer, IFileOrFolder fileOrFolder, int oldStartingIndex, int newStartingIndex)
	{
		if (oldStartingIndex == newStartingIndex) throw new InvalidOperationException("Old and new starting indexes are the same");
		var treeItem = treeItemContainer.Value!;
		var isFile = fileOrFolder is SharpIdeFile;
		if (isFile)
		{
			var sharpIdeParent = fileOrFolder.Parent as SharpIdeFolder;
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
	    await this.InvokeAsync(() =>
	    {
		    item?.SharpIdeDisposable?.Dispose();
		    item?.Free();
	    });
	}
}
