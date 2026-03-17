using Godot;
using SharpIDE.Application.Features.Git;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.Git;

public partial class CommitPanel : MarginContainer
{
    private enum ChangedFileContextAction
    {
        OpenFile = 1,
        RevealInFileExplorer = 2,
        StageFile = 3,
        UnstageFile = 4,
        DiscardUnstagedChanges = 5,
        DiscardFileChanges = 6,
        DeleteUntrackedFile = 7,
        CopyRelativePath = 8
    }

    private enum StashRootContextAction
    {
        ApplyStash = 1,
        PopStash = 2,
        DropStash = 3,
        CopyStashName = 4
    }

    private enum StashFileContextAction
    {
        OpenStashDiff = 1,
        ApplyChanges = 2,
        CopyRelativePath = 3
    }

    private sealed class GitStashTreeFileNode
    {
        public required GitStashEntry Entry { get; init; }
        public required GitStashChangedFile File { get; init; }
    }

    private Label _branchLabel = null!;
    private Button _refreshButton = null!;
    private VBoxContainer _contentVBox = null!;
    private Label _emptyStateLabel = null!;
    private VSplitContainer _topSplit = null!;
    private Tree _changesTree = null!;
    private TextEdit _commitMessageTextEdit = null!;
    private Button _commitButton = null!;
    private Button _commitAndPushButton = null!;
    private Button _stashButton = null!;
    private Tree _stashesTree = null!;

    private GitSnapshot _snapshot = GitSnapshot.Empty();
    private SharpIdeSolutionModel? _solution;
    private bool _gitCliAvailable;
    private bool _updatingTrees;
    private bool _isBusy;
    private DateTimeOffset _suppressRepositoryRefreshUntil = DateTimeOffset.MinValue;
    private CancellationTokenSource? _refreshDebounceCts;
    private bool _commitSplitInitialized;
    private bool _suppressChangesTreePreviewOnNextSelection;

    [Inject] private readonly GitService _gitService = null!;
    [Inject] private readonly GitRepositoryMonitor _gitRepositoryMonitor = null!;
    [Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;

    public override void _Ready()
    {
        _branchLabel = GetNode<Label>("%BranchLabel");
        _refreshButton = GetNode<Button>("%RefreshButton");
        _contentVBox = GetNode<VBoxContainer>("%ContentVBox");
        _emptyStateLabel = GetNode<Label>("%EmptyStateLabel");
        _topSplit = GetNode<VSplitContainer>("%TopSplit");
        _changesTree = GetNode<Tree>("%ChangesTree");
        _commitMessageTextEdit = GetNode<TextEdit>("%CommitMessageTextEdit");
        _commitButton = GetNode<Button>("%CommitButton");
        _commitAndPushButton = GetNode<Button>("%CommitAndPushButton");
        _stashButton = GetNode<Button>("%StashButton");
        _stashesTree = GetNode<Tree>("%StashesTree");

        ConfigureTrees();
        UpdateCommitMessageEditorLayout();

        _refreshButton.Pressed += () => _ = Task.GodotRun(() => RefreshSnapshotAsync());
        _commitButton.Pressed += () => _ = Task.GodotRun(CommitAsync);
        _commitAndPushButton.Pressed += () => _ = Task.GodotRun(CommitAndPushAsync);
        _stashButton.Pressed += () => _ = Task.GodotRun(StashSelectedAsync);
        _commitMessageTextEdit.TextChanged += UpdateActionState;
        _changesTree.ItemEdited += OnChangesTreeItemEdited;
        _changesTree.ItemSelected += OnChangesTreeItemSelected;
        _changesTree.ItemActivated += OnChangesTreeItemActivated;
        _changesTree.GuiInput += OnChangesTreeGuiInput;
        _stashesTree.ItemSelected += OnStashesTreeItemSelected;
        _stashesTree.ItemActivated += OnStashesTreeItemActivated;
        _stashesTree.GuiInput += OnStashesTreeGuiInput;

        _gitRepositoryMonitor.RepositoryChanged.Subscribe(OnRepositoryChanged);
        _ = Task.GodotRun(AsyncReady);
    }

    public override void _Notification(int what)
    {
        if ((what == NotificationThemeChanged || what == NotificationResized) && IsNodeReady())
        {
            UpdateCommitMessageEditorLayout();
        }
    }

    public override void _ExitTree()
    {
        _refreshDebounceCts?.Cancel();
        _gitRepositoryMonitor.RepositoryChanged.Unsubscribe(OnRepositoryChanged);
        _gitRepositoryMonitor.Stop();
    }

    private void ConfigureTrees()
    {
        _changesTree.HideRoot = true;
        _changesTree.Columns = 3;
        _changesTree.SelectMode = Tree.SelectModeEnum.Row;
        _changesTree.SetColumnExpand(0, false);
        _changesTree.SetColumnCustomMinimumWidth(0, 28);
        _changesTree.SetColumnExpand(1, true);
        _changesTree.SetColumnExpandRatio(1, 5);
        _changesTree.SetColumnClipContent(1, true);
        _changesTree.SetColumnExpand(2, true);
        _changesTree.SetColumnExpandRatio(2, 2);
        _changesTree.SetColumnCustomMinimumWidth(2, 40);
        _changesTree.SetColumnClipContent(2, true);
        _changesTree.ColumnTitlesVisible = false;

        _stashesTree.HideRoot = true;
        _stashesTree.Columns = 1;
        _stashesTree.SelectMode = Tree.SelectModeEnum.Row;
        _stashesTree.ColumnTitlesVisible = false;
    }

    private void UpdateCommitMessageEditorLayout()
    {
        if (_commitMessageTextEdit is null || _topSplit is null) return;

        var styleBox = _commitMessageTextEdit.GetThemeStylebox("normal");
        var verticalPadding = styleBox is null
            ? 0f
            : styleBox.GetContentMargin(Side.Top) + styleBox.GetContentMargin(Side.Bottom);
        var targetHeight = (4f * _commitMessageTextEdit.GetLineHeight()) + verticalPadding;
        _commitMessageTextEdit.CustomMinimumSize = new Vector2(0f, Mathf.Ceil(targetHeight));
        _commitMessageTextEdit.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        CallDeferred(MethodName.ApplyInitialCommitSplitOffset);
    }

    private void ApplyInitialCommitSplitOffset()
    {
        if (_commitSplitInitialized || _topSplit is null || _commitMessageTextEdit is null) return;
        if (_topSplit.Size.Y <= 0f) return;

        var commitVBox = _commitMessageTextEdit.GetParent<Container>();
        var commitPanel = commitVBox?.GetParent<Control>();
        if (commitVBox is null || commitPanel is null) return;

        var actionRowHeight = commitVBox.GetChildCount() > 1
            ? commitVBox.GetChild<Control>(1).Size.Y
            : 0f;
        var spacing = commitVBox.GetThemeConstant("separation");
        var panelStyleBox = commitPanel.GetThemeStylebox("panel");
        var panelPadding = panelStyleBox is null
            ? 0f
            : panelStyleBox.GetContentMargin(Side.Top) + panelStyleBox.GetContentMargin(Side.Bottom);
        var desiredCommitHeight = _commitMessageTextEdit.CustomMinimumSize.Y + actionRowHeight + spacing + panelPadding;
        var maxOffset = Math.Max(0f, _topSplit.Size.Y - desiredCommitHeight);
        SetSplitOffset(_topSplit, (int)Mathf.Round(maxOffset));
        _commitSplitInitialized = true;
    }

    private static void SetSplitOffset(SplitContainer splitContainer, int splitOffset)
    {
        var offsets = splitContainer.SplitOffsets;
        if (offsets.Length == 0)
        {
            offsets = [splitOffset];
        }
        else
        {
            offsets[0] = splitOffset;
        }

        splitContainer.SplitOffsets = offsets;
    }

    private async Task AsyncReady()
    {
        await _solutionAccessor.SolutionReadyTcs.Task;
        _solution = _solutionAccessor.SolutionModel;
        _gitCliAvailable = await _gitService.IsGitCliAvailable();
        await RefreshSnapshotAsync();
    }

    private async Task OnRepositoryChanged()
    {
        if (DateTimeOffset.UtcNow < _suppressRepositoryRefreshUntil) return;

        var previousRefreshDebounceCts = _refreshDebounceCts;
        _refreshDebounceCts = new CancellationTokenSource();
        if (previousRefreshDebounceCts is not null)
        {
            await previousRefreshDebounceCts.CancelAsync();
            previousRefreshDebounceCts.Dispose();
        }
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), _refreshDebounceCts.Token);
            await RefreshSnapshotAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_solution is null || _isBusy) return;

        var snapshot = await _gitService.GetSnapshot(_solution.FilePath, commitCount: 50);
        _snapshot = snapshot;
        ApplySnapshotToSolution();

        if (snapshot.Repository.IsRepositoryDiscovered)
        {
            _gitRepositoryMonitor.Start(snapshot.Repository.RepoRootPath, snapshot.Repository.GitDirectoryPath);
        }
        else
        {
            _gitRepositoryMonitor.Stop();
        }

        await this.InvokeAsync(() =>
        {
            PopulateSnapshotUi();
            GodotGlobalEvents.Instance.GitStatusesChanged.InvokeParallelFireAndForget();
        });
    }

    private void PopulateSnapshotUi()
    {
        var hasRepository = _snapshot.Repository.IsRepositoryDiscovered;
        _branchLabel.Text = hasRepository
            ? (_snapshot.Repository.IsDetachedHead ? "Detached HEAD" : _snapshot.Repository.BranchDisplayName)
            : "No repository";
        _contentVBox.Visible = hasRepository;
        _emptyStateLabel.Visible = !hasRepository;
        _emptyStateLabel.Text = "No git repository was found for the current solution.";

        PopulateChangesTree();
        PopulateStashesTree();
        UpdateActionState();
    }

    private void PopulateChangesTree()
    {
        _updatingTrees = true;
        try
        {
            _changesTree.Clear();
            var root = _changesTree.CreateItem();
            var changedEntries = _snapshot.WorkingTreeEntries.Where(entry => entry.Group is GitWorkingTreeGroup.ChangedFiles).ToList();
            var unversionedEntries = _snapshot.WorkingTreeEntries.Where(entry => entry.Group is GitWorkingTreeGroup.UnversionedFiles).ToList();

            if (changedEntries.Count is 0 && unversionedEntries.Count is 0)
            {
                var noChangesItem = _changesTree.CreateItem(root);
                noChangesItem.SetText(1, "No local changes");
                _changesTree.SetSelected(noChangesItem, 1);
                return;
            }

            CreateGroupItem(root, GitWorkingTreeGroup.ChangedFiles, "Changed Files", changedEntries);
            CreateGroupItem(root, GitWorkingTreeGroup.UnversionedFiles, "Unversioned Files", unversionedEntries);
        }
        finally
        {
            _updatingTrees = false;
        }
    }

    private void CreateGroupItem(TreeItem root, GitWorkingTreeGroup group, string title, IReadOnlyList<GitWorkingTreeEntry> entries)
    {
        if (entries.Count is 0) return;

        var groupItem = _changesTree.CreateItem(root);
        groupItem.SetAssociatedValue(group);
        groupItem.SetCellMode(0, TreeItem.TreeCellMode.Check);
        groupItem.SetEditable(0, true);
        ApplyCheckboxState(groupItem, GetAggregateStageDisplayState(entries));
        groupItem.SetText(1, $"{title} {entries.Count}");
        groupItem.Collapsed = false;

        foreach (var entry in entries)
        {
            var item = _changesTree.CreateItem(groupItem);
            item.SetAssociatedValue(entry);
            item.SetCellMode(0, TreeItem.TreeCellMode.Check);
            item.SetEditable(0, true);
            ApplyCheckboxState(item, entry.StageDisplayState);
            item.SetText(1, $"{GetStatusPrefix(entry)}{entry.FileName}");
            item.SetText(2, entry.DirectoryDisplayPath);
            item.SetTextAlignment(2, HorizontalAlignment.Right);
            item.SetTextOverrunBehavior(1, TextServer.OverrunBehavior.TrimEllipsis);
            item.SetTextOverrunBehavior(2, TextServer.OverrunBehavior.TrimEllipsis);
            item.SetTooltipText(1, entry.RepoRelativePath);
            item.SetTooltipText(2, entry.RepoRelativePath);
            ApplyEntryItemStyles(item, entry);
        }
    }

    private void PopulateStashesTree()
    {
        _updatingTrees = true;
        try
        {
            _stashesTree.Clear();
            var root = _stashesTree.CreateItem();
            if (_snapshot.Stashes.Count is 0)
            {
                var emptyItem = _stashesTree.CreateItem(root);
                emptyItem.SetText(0, "No stashes");
                _stashesTree.SetSelected(emptyItem, 0);
                return;
            }

            foreach (var stash in _snapshot.Stashes)
            {
                var stashItem = _stashesTree.CreateItem(root);
                stashItem.SetAssociatedValue(stash);
                stashItem.SetText(0, $"{stash.StashRef}  {stash.Message}");
                stashItem.Collapsed = true;

                foreach (var file in stash.Files)
                {
                    var child = _stashesTree.CreateItem(stashItem);
                    child.SetAssociatedValue(new GitStashTreeFileNode
                    {
                        Entry = stash,
                        File = file
                    });
                    child.SetText(0, $"{GetStatusPrefix(file.StatusCode)}{file.DisplayPath}");
                    child.SetTooltipText(0, file.RepoRelativePath);
                    ApplyStashFileItemStyles(child, file);
                }
            }
        }
        finally
        {
            _updatingTrees = false;
        }
    }

    private void OnChangesTreeItemEdited()
    {
        if (_updatingTrees || _snapshot.Repository.IsRepositoryDiscovered is false) return;

        CallDeferred(MethodName.ClearChangesTreePreviewSuppression);
        var editedItem = _changesTree.GetEdited();
        if (editedItem is null) return;

        _ = Task.GodotRun(async () =>
        {
            try
            {
                if (editedItem.GetAssociatedValue<GitWorkingTreeEntry>() is { } entry)
                {
                    await ToggleStageAsync([entry.AbsolutePath], editedItem.IsChecked(0));
                    return;
                }

                if (editedItem.TryGetAssociatedValue<GitWorkingTreeGroup>(out var group))
                {
                    var paths = _snapshot.WorkingTreeEntries
                        .Where(entry => entry.Group == group)
                        .Select(entry => entry.AbsolutePath)
                        .ToList();
                    await ToggleStageAsync(paths, editedItem.IsChecked(0));
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("Git Action Failed", ex.Message);
                await RefreshSnapshotAsync();
            }
        });
    }

    private void OnChangesTreeItemSelected()
    {
        if (_updatingTrees) return;
        if (_suppressChangesTreePreviewOnNextSelection)
        {
            _suppressChangesTreePreviewOnNextSelection = false;
            return;
        }

        var selected = _changesTree.GetSelected();
        var entry = selected?.GetAssociatedValue<GitWorkingTreeEntry>();
        if (entry is null) return;

        GodotGlobalEvents.Instance.GitFilePreviewRequested.InvokeParallelFireAndForget(entry.AbsolutePath);
    }

    private void OnChangesTreeItemActivated()
    {
        var selected = _changesTree.GetSelected();
        var entry = selected?.GetAssociatedValue<GitWorkingTreeEntry>();
        if (entry is null) return;

        OpenSolutionFileOrReveal(entry.AbsolutePath);
    }

    private void OnChangesTreeGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mouseEvent) return;

        if (mouseEvent.ButtonIndex is MouseButton.Left)
        {
            _suppressChangesTreePreviewOnNextSelection = IsChangesTreeCheckboxClick(mouseEvent.Position);
            return;
        }

        if (mouseEvent.ButtonIndex is not MouseButton.Right) return;

        var selected = SelectTreeItemAtPosition(_changesTree, mouseEvent.Position);
        var entry = selected?.GetAssociatedValue<GitWorkingTreeEntry>();
        if (entry is null) return;

        OpenChangedFileContextMenu(entry);
        _changesTree.AcceptEvent();
    }

    private async Task ToggleStageAsync(IEnumerable<string> paths, bool shouldStage)
    {
        if (_snapshot.Repository.IsRepositoryDiscovered is false || _isBusy) return;

        var pathList = paths
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (pathList.Count is 0) return;

        _isBusy = true;
        await this.InvokeAsync(UpdateActionState);
        try
        {
            if (shouldStage)
            {
                await _gitService.StagePaths(_snapshot.Repository.RepoRootPath, pathList);
            }
            else
            {
                await _gitService.UnstagePaths(_snapshot.Repository.RepoRootPath, pathList);
            }

            _suppressRepositoryRefreshUntil = DateTimeOffset.UtcNow.AddMilliseconds(700);
            UpdateLocalStageState(pathList, shouldStage);
            await this.InvokeAsync(() =>
            {
                RefreshChangesTreeStageStateInPlace();
                UpdateActionState();
                GodotGlobalEvents.Instance.GitStatusesChanged.InvokeParallelFireAndForget();
            });
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("Git Action Failed", ex.Message);
            _isBusy = false;
            await RefreshSnapshotAsync();
        }
        finally
        {
            _isBusy = false;
            await this.InvokeAsync(UpdateActionState);
        }
    }

    private async Task CommitAsync()
    {
        var commitMessage = _commitMessageTextEdit.Text.Trim();
        if (string.IsNullOrWhiteSpace(commitMessage) || _snapshot.StagedEntryCount is 0) return;

        await RunGitActionAsync(async () =>
        {
            await _gitService.Commit(_snapshot.Repository.RepoRootPath, commitMessage);
            await this.InvokeAsync(() => _commitMessageTextEdit.Text = string.Empty);
        });
    }

    private async Task CommitAndPushAsync()
    {
        if (_gitCliAvailable is false)
        {
            await ShowErrorDialogAsync("Git CLI Not Available", "Commit and push requires the `git` executable to be available on PATH.");
            return;
        }

        var commitMessage = _commitMessageTextEdit.Text.Trim();
        if (string.IsNullOrWhiteSpace(commitMessage) || _snapshot.StagedEntryCount is 0) return;

        await RunGitActionAsync(async () =>
        {
            await _gitService.Commit(_snapshot.Repository.RepoRootPath, commitMessage);
            await _gitService.Push(_snapshot.Repository.RepoRootPath);
            await this.InvokeAsync(() => _commitMessageTextEdit.Text = string.Empty);
        });
    }

    private async Task StashSelectedAsync()
    {
        if (_gitCliAvailable is false)
        {
            await ShowErrorDialogAsync("Git CLI Not Available", "Selective stash requires the `git` executable to be available on PATH.");
            return;
        }

        var selectedEntries = _snapshot.WorkingTreeEntries.Where(entry => entry.IsStaged).ToList();
        if (selectedEntries.Count is 0) return;

        var message = _commitMessageTextEdit.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            var branch = _snapshot.Repository.IsDetachedHead ? "head" : _snapshot.Repository.BranchDisplayName;
            message = $"stash: {branch} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
        }

        await RunGitActionAsync(() => _gitService.SelectiveStash(
            _snapshot.Repository.RepoRootPath,
            selectedEntries.Select(entry => entry.AbsolutePath),
            message,
            includeUntracked: selectedEntries.Any(entry => entry.Group is GitWorkingTreeGroup.UnversionedFiles)));
    }

    private void OnStashesTreeItemSelected()
    {
        if (_updatingTrees) return;
    }

    private void OnStashesTreeItemActivated()
    {
        var selected = _stashesTree.GetSelected();
        if (selected is null) return;

        if (selected.GetAssociatedValue<GitStashTreeFileNode>() is { } fileNode)
        {
            PreviewStashFile(fileNode.Entry, fileNode.File);
            return;
        }

        if (selected.GetAssociatedValue<GitStashEntry>() is not null)
        {
            selected.Collapsed = !selected.Collapsed;
        }
    }

    private void OnStashesTreeGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouseEvent) return;

        var selected = SelectTreeItemAtPosition(_stashesTree, mouseEvent.Position);
        if (selected is null) return;

        if (selected.GetAssociatedValue<GitStashTreeFileNode>() is { } fileNode)
        {
            OpenStashFileContextMenu(fileNode);
            _stashesTree.AcceptEvent();
            return;
        }

        if (selected.GetAssociatedValue<GitStashEntry>() is { } entry)
        {
            OpenStashRootContextMenu(entry);
            _stashesTree.AcceptEvent();
        }
    }

    private void OpenChangedFileContextMenu(GitWorkingTreeEntry entry)
    {
        var menu = new PopupMenu();
        AddChild(menu);

        if (TryResolveSolutionFile(entry.AbsolutePath, out _))
        {
            menu.AddItem("Open File", (int)ChangedFileContextAction.OpenFile);
        }

        menu.AddItem("Reveal in File Explorer", (int)ChangedFileContextAction.RevealInFileExplorer);
        menu.AddSeparator();

        if (entry.StageDisplayState is GitStageDisplayState.Unstaged or GitStageDisplayState.Partial)
        {
            menu.AddItem("Stage File", (int)ChangedFileContextAction.StageFile);
        }

        if (entry.StageDisplayState is GitStageDisplayState.Staged or GitStageDisplayState.Partial)
        {
            menu.AddItem("Unstage File", (int)ChangedFileContextAction.UnstageFile);
        }

        if (entry.IsTracked && entry.StageDisplayState is GitStageDisplayState.Unstaged or GitStageDisplayState.Partial)
        {
            menu.AddItem("Discard Unstaged Changes", (int)ChangedFileContextAction.DiscardUnstagedChanges);
        }

        if (entry.IsTracked && entry.StageDisplayState is GitStageDisplayState.Staged)
        {
            menu.AddItem("Discard File Changes", (int)ChangedFileContextAction.DiscardFileChanges);
        }

        if (!entry.IsTracked)
        {
            menu.AddItem("Delete Untracked File", (int)ChangedFileContextAction.DeleteUntrackedFile);
        }

        menu.AddSeparator();
        menu.AddItem("Copy Relative Path", (int)ChangedFileContextAction.CopyRelativePath);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id => _ = Task.GodotRun(() => HandleChangedFileContextActionAsync(entry, (ChangedFileContextAction)id));
        PopupMenuAtMouse(menu);
    }

    private async Task HandleChangedFileContextActionAsync(GitWorkingTreeEntry entry, ChangedFileContextAction action)
    {
        switch (action)
        {
            case ChangedFileContextAction.OpenFile:
                OpenSolutionFileOrReveal(entry.AbsolutePath);
                break;
            case ChangedFileContextAction.RevealInFileExplorer:
                RevealInFileExplorer(entry.AbsolutePath);
                break;
            case ChangedFileContextAction.StageFile:
                await ToggleStageAsync([entry.AbsolutePath], shouldStage: true);
                break;
            case ChangedFileContextAction.UnstageFile:
                await ToggleStageAsync([entry.AbsolutePath], shouldStage: false);
                break;
            case ChangedFileContextAction.DiscardUnstagedChanges:
                if (await ConfirmAsync("Discard Unstaged Changes", $"Discard unstaged changes in '{entry.RepoRelativePath}'?"))
                {
                    await RunGitActionAsync(() => _gitService.DiscardUnstagedPaths(_snapshot.Repository.RepoRootPath, [entry.AbsolutePath]));
                    GodotGlobalEvents.Instance.GitFilePreviewRequested.InvokeParallelFireAndForget(entry.AbsolutePath);
                }
                break;
            case ChangedFileContextAction.DiscardFileChanges:
                if (await ConfirmAsync("Discard File Changes", $"Discard all changes in '{entry.RepoRelativePath}'?"))
                {
                    await RunGitActionAsync(() => _gitService.DiscardPaths(_snapshot.Repository.RepoRootPath, [entry.AbsolutePath]));
                    GodotGlobalEvents.Instance.GitFilePreviewRequested.InvokeParallelFireAndForget(entry.AbsolutePath);
                }
                break;
            case ChangedFileContextAction.DeleteUntrackedFile:
                if (await ConfirmAsync("Delete Untracked File", $"Delete untracked file '{entry.RepoRelativePath}'?"))
                {
                    await RunGitActionAsync(() => _gitService.DiscardPaths(_snapshot.Repository.RepoRootPath, [entry.AbsolutePath]));
                    GodotGlobalEvents.Instance.GitFilePreviewRequested.InvokeParallelFireAndForget(entry.AbsolutePath);
                }
                break;
            case ChangedFileContextAction.CopyRelativePath:
                DisplayServer.ClipboardSet(entry.RepoRelativePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private void OpenStashRootContextMenu(GitStashEntry entry)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("Apply Stash", (int)StashRootContextAction.ApplyStash);
        menu.AddItem("Pop Stash", (int)StashRootContextAction.PopStash);
        menu.AddItem("Drop Stash", (int)StashRootContextAction.DropStash);
        menu.AddSeparator();
        menu.AddItem("Copy Stash Name", (int)StashRootContextAction.CopyStashName);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id => _ = Task.GodotRun(() => HandleStashRootContextActionAsync(entry, (StashRootContextAction)id));
        PopupMenuAtMouse(menu);
    }

    private async Task HandleStashRootContextActionAsync(GitStashEntry entry, StashRootContextAction action)
    {
        switch (action)
        {
            case StashRootContextAction.ApplyStash:
                await RunGitActionAsync(() => _gitService.ApplyStash(_snapshot.Repository.RepoRootPath, entry.StashRef));
                break;
            case StashRootContextAction.PopStash:
                if (await ConfirmAsync("Pop Stash", $"Pop '{entry.StashRef}'?"))
                {
                    await RunGitActionAsync(() => _gitService.PopStash(_snapshot.Repository.RepoRootPath, entry.StashRef));
                }
                break;
            case StashRootContextAction.DropStash:
                if (await ConfirmAsync("Drop Stash", $"Drop '{entry.StashRef}'?"))
                {
                    await RunGitActionAsync(() => _gitService.DropStash(_snapshot.Repository.RepoRootPath, entry.StashRef));
                }
                break;
            case StashRootContextAction.CopyStashName:
                DisplayServer.ClipboardSet(entry.StashRef);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private void OpenStashFileContextMenu(GitStashTreeFileNode fileNode)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("Open Stash Diff", (int)StashFileContextAction.OpenStashDiff);
        menu.AddSeparator();
        menu.AddItem("Apply Changes", (int)StashFileContextAction.ApplyChanges);
        menu.AddSeparator();
        menu.AddItem("Copy Relative Path", (int)StashFileContextAction.CopyRelativePath);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id => _ = Task.GodotRun(() => HandleStashFileContextActionAsync(fileNode, (StashFileContextAction)id));
        PopupMenuAtMouse(menu);
    }

    private async Task HandleStashFileContextActionAsync(GitStashTreeFileNode fileNode, StashFileContextAction action)
    {
        switch (action)
        {
            case StashFileContextAction.OpenStashDiff:
                PreviewStashFile(fileNode.Entry, fileNode.File);
                break;
            case StashFileContextAction.ApplyChanges:
                await RunGitActionAsync(() => _gitService.ApplyStashFileChanges(new GitStashFileDiffRequest
                {
                    RepoRootPath = _snapshot.Repository.RepoRootPath,
                    StashRef = fileNode.Entry.StashRef,
                    RepoRelativePath = fileNode.File.RepoRelativePath,
                    OldRepoRelativePath = fileNode.File.OldRepoRelativePath,
                    StatusCode = fileNode.File.StatusCode,
                    ContentKind = fileNode.File.ContentKind
                }));
                break;
            case StashFileContextAction.CopyRelativePath:
                DisplayServer.ClipboardSet(fileNode.File.RepoRelativePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private void PreviewStashFile(GitStashEntry entry, GitStashChangedFile file)
    {
        GodotGlobalEvents.Instance.GitStashDiffRequested.InvokeParallelFireAndForget(new GitStashFileDiffRequest
        {
            RepoRootPath = _snapshot.Repository.RepoRootPath,
            StashRef = entry.StashRef,
            RepoRelativePath = file.RepoRelativePath,
            OldRepoRelativePath = file.OldRepoRelativePath,
            StatusCode = file.StatusCode,
            ContentKind = file.ContentKind
        });
    }

    private async Task RunGitActionAsync(Func<Task> action)
    {
        if (_snapshot.Repository.IsRepositoryDiscovered is false || _isBusy) return;

        _isBusy = true;
        await this.InvokeAsync(UpdateActionState);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("Git Action Failed", ex.Message);
        }
        finally
        {
            _isBusy = false;
            await RefreshSnapshotAsync();
        }
    }

    private void ApplySnapshotToSolution()
    {
        if (_solution is null) return;

        foreach (var file in _solution.AllFiles.Values)
        {
            file.GitStatus = GitFileStatus.Unaltered;
        }

        foreach (var entry in _snapshot.WorkingTreeEntries)
        {
            if (_solution.AllFiles.GetValueOrDefault(entry.AbsolutePath) is { } file)
            {
                file.GitStatus = GitStatusMapper.ToSharpIdeFileStatus(entry);
            }
        }
    }

    private void UpdateActionState()
    {
        var hasRepository = _snapshot.Repository.IsRepositoryDiscovered;
        var hasStagedEntries = _snapshot.StagedEntryCount > 0;
        var hasCommitMessage = string.IsNullOrWhiteSpace(_commitMessageTextEdit.Text) is false;

        _refreshButton.Disabled = _isBusy;
        _commitMessageTextEdit.Editable = !_isBusy && hasRepository;
        _commitButton.Disabled = _isBusy || !hasRepository || !hasStagedEntries || !hasCommitMessage;
        _commitAndPushButton.Disabled = _isBusy || !hasRepository || !hasStagedEntries || !hasCommitMessage || !_gitCliAvailable;
        _stashButton.Disabled = _isBusy || !hasRepository || !hasStagedEntries || !_gitCliAvailable;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ConfirmationDialog
        {
            Title = title,
            DialogText = message
        };
        AddChild(dialog);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Confirmed += () => tcs.TrySetResult(true);
        dialog.Canceled += () => tcs.TrySetResult(false);
        dialog.CloseRequested += () => tcs.TrySetResult(false);
        dialog.PopupCentered();

        var result = await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
        return result;
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var dialog = new AcceptDialog
        {
            Title = title,
            DialogText = message
        };
        AddChild(dialog);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Confirmed += () => tcs.TrySetResult();
        dialog.CloseRequested += () => tcs.TrySetResult();
        dialog.PopupCentered();
        await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
    }

    private static GitStageDisplayState GetAggregateStageDisplayState(IReadOnlyList<GitWorkingTreeEntry> entries)
    {
        if (entries.Count is 0) return GitStageDisplayState.Unstaged;
        if (entries.All(entry => entry.StageDisplayState is GitStageDisplayState.Staged))
        {
            return GitStageDisplayState.Staged;
        }

        return entries.Any(entry => entry.IsStaged)
            ? GitStageDisplayState.Partial
            : GitStageDisplayState.Unstaged;
    }

    private static void ApplyCheckboxState(TreeItem item, GitStageDisplayState displayState)
    {
        switch (displayState)
        {
            case GitStageDisplayState.Staged:
                item.SetChecked(0, true);
                item.SetIndeterminate(0, false);
                break;
            case GitStageDisplayState.Partial:
                item.SetChecked(0, false);
                item.SetIndeterminate(0, true);
                break;
            default:
                item.SetChecked(0, false);
                item.SetIndeterminate(0, false);
                break;
        }
    }

    private static string GetStatusPrefix(GitWorkingTreeEntry entry)
    {
        if (entry.Status.HasFlag(GitWorkingTreeStatus.Conflicted)) return "! ";
        if (entry.Status.HasFlag(GitWorkingTreeStatus.Deleted)) return "D ";
        if (entry.Status.HasFlag(GitWorkingTreeStatus.Renamed)) return "R ";
        if (entry.Status.HasFlag(GitWorkingTreeStatus.TypeChange)) return "T ";
        if (entry.Status.HasFlag(GitWorkingTreeStatus.Unversioned)) return "A ";
        return "M ";
    }

    private static string GetStatusPrefix(string statusCode)
    {
        return statusCode[..Math.Min(1, statusCode.Length)] switch
        {
            "A" => "A ",
            "D" => "D ",
            "R" => "R ",
            "T" => "T ",
            "U" => "! ",
            _ => "M "
        };
    }

    private static Color GetColorForEntry(GitWorkingTreeEntry entry)
    {
        if (entry.Status.HasFlag(GitWorkingTreeStatus.Conflicted))
        {
            return new Color(1f, 0.45f, 0.38f);
        }

        return entry.Group switch
        {
            GitWorkingTreeGroup.UnversionedFiles => GitColours.GitNewFileColour,
            _ => GitColours.GitEditedFileColour
        };
    }

    private static Color GetColorForStashFile(GitStashChangedFile file)
    {
        return file.StatusCode switch
        {
            "A" => GitColours.GitNewFileColour,
            "D" => new Color(1f, 0.45f, 0.38f),
            _ => GitColours.GitEditedFileColour
        };
    }

    private void ApplyEntryItemStyles(TreeItem item, GitWorkingTreeEntry entry)
    {
        var rowColor = GetColorForEntry(entry);
        item.SetCustomColor(0, rowColor with { A = 0.8f });
        item.SetCustomColor(1, rowColor);
        item.SetCustomColor(2, rowColor with { A = 0.7f });

        var hasStagedContent = entry.IsStaged;
        var stagedBackground = hasStagedContent
            ? new Color(0.20f, 0.27f, 0.35f, 0.55f)
            : Colors.Transparent;
        item.SetCustomBgColor(0, stagedBackground, hasStagedContent);
        item.SetCustomBgColor(1, stagedBackground, hasStagedContent);
        item.SetCustomBgColor(2, stagedBackground, hasStagedContent);
    }

    private void ApplyStashFileItemStyles(TreeItem item, GitStashChangedFile file)
    {
        var color = GetColorForStashFile(file);
        item.SetCustomColor(0, color);
    }

    private void RefreshChangesTreeStageStateInPlace()
    {
        _updatingTrees = true;
        try
        {
            var root = _changesTree.GetRoot();
            if (root is null) return;

            for (var groupItem = root.GetFirstChild(); groupItem is not null; groupItem = groupItem.GetNext())
            {
                if (!groupItem.TryGetAssociatedValue<GitWorkingTreeGroup>(out var group)) continue;

                var groupEntries = _snapshot.WorkingTreeEntries
                    .Where(entry => entry.Group == group)
                    .ToDictionary(entry => entry.AbsolutePath, StringComparer.OrdinalIgnoreCase);

                ApplyCheckboxState(groupItem, GetAggregateStageDisplayState(groupEntries.Values.ToList()));

                for (var item = groupItem.GetFirstChild(); item is not null; item = item.GetNext())
                {
                    var entry = item.GetAssociatedValue<GitWorkingTreeEntry>();
                    if (entry is null) continue;
                    if (!groupEntries.TryGetValue(entry.AbsolutePath, out var updatedEntry)) continue;

                    item.SetAssociatedValue(updatedEntry);
                    ApplyCheckboxState(item, updatedEntry.StageDisplayState);
                    ApplyEntryItemStyles(item, updatedEntry);
                }
            }
        }
        finally
        {
            _updatingTrees = false;
        }
    }

    private void UpdateLocalStageState(IReadOnlyCollection<string> absolutePaths, bool isStaged)
    {
        var pathSet = absolutePaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _snapshot = new GitSnapshot
        {
            Repository = _snapshot.Repository,
            RecentCommits = _snapshot.RecentCommits,
            Stashes = _snapshot.Stashes,
            WorkingTreeEntries = _snapshot.WorkingTreeEntries
                .Select(entry => pathSet.Contains(entry.AbsolutePath)
                    ? new GitWorkingTreeEntry
                    {
                        AbsolutePath = entry.AbsolutePath,
                        RepoRelativePath = entry.RepoRelativePath,
                        Group = entry.Group,
                        Status = entry.Status,
                        StageDisplayState = isStaged ? GitStageDisplayState.Staged : GitStageDisplayState.Unstaged,
                        IsTracked = entry.IsTracked,
                        IsStaged = isStaged
                    }
                    : entry)
                .ToList()
        };

        ApplySnapshotToSolution();
    }

    private bool TryResolveSolutionFile(string absolutePath, out SharpIdeFile file)
    {
        file = null!;
        if (_solution is null) return false;

        if (_solution.AllFiles.GetValueOrDefault(absolutePath) is { } exactFile)
        {
            file = exactFile;
            return true;
        }

        var normalizedPath = Path.GetFullPath(absolutePath);
        var matchedFile = _solution.AllFiles.Values.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (matchedFile is null) return false;

        file = matchedFile;
        return true;
    }

    private void OpenSolutionFileOrReveal(string absolutePath)
    {
        if (TryResolveSolutionFile(absolutePath, out var file))
        {
            GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(file, null);
            return;
        }

        RevealInFileExplorer(absolutePath);
    }

    private static void RevealInFileExplorer(string absolutePath)
    {
        var revealPath = File.Exists(absolutePath) || Directory.Exists(absolutePath)
            ? absolutePath
            : Path.GetDirectoryName(absolutePath) ?? absolutePath;
        OS.ShellShowInFileManager(revealPath);
    }

    private void PopupMenuAtMouse(PopupMenu menu)
    {
        var mousePosition = GetGlobalMousePosition();
        menu.Position = new Vector2I((int)mousePosition.X, (int)mousePosition.Y);
        menu.Popup();
    }

    private static TreeItem? SelectTreeItemAtPosition(Tree tree, Vector2 mousePosition)
    {
        var item = tree.GetItemAtPosition(mousePosition);
        if (item is null) return null;

        tree.SetSelected(item, 0);
        return item;
    }

    private bool IsChangesTreeCheckboxClick(Vector2 mousePosition)
    {
        var item = _changesTree.GetItemAtPosition(mousePosition);
        if (item is null) return false;

        var column = _changesTree.GetColumnAtPosition(mousePosition);
        return column == 0 &&
               item.IsEditable(0) &&
               item.GetCellMode(0) == TreeItem.TreeCellMode.Check;
    }

    private void ClearChangesTreePreviewSuppression()
    {
        _suppressChangesTreePreviewOnNextSelection = false;
    }
}
