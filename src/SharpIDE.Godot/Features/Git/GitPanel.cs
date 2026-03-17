using Godot;
using SharpIDE.Application.Features.Git;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Problems;
using ResetMode = LibGit2Sharp.ResetMode;

namespace SharpIDE.Godot.Features.Git;

public partial class GitPanel : Control
{
    private enum RefContextAction
    {
        Checkout = 1,
        NewBranchFrom = 2,
        ShowDiffWithWorkingTree = 3, // TODO
        ShowDiffCurrentBranchWithSelected = 4, //TODO
        RebaseCurrentOntoSelected = 5,
        MergeSelectedIntoCurrent = 6,
        UpdateCurrentBranch = 7,
        PushCurrentBranch = 8,
        Rename = 9,
        DeleteLocalBranch = 10,
        PushTag = 11,
        DeleteLocalTag = 12,
        DeleteRemoteTag = 13
    }

    private enum HistoryContextAction
    {
        CopyRevisionNumber = 1,
        CreatePatch = 2,
        CherryPick = 3,
        CheckoutRevision = 4,
        CompareWithWorkingTree = 5,
        ResetCurrentBranchToHere = 6,
        RevertCommit = 7,
        UndoCommit = 8,
        EditCommitMessage = 9,
        NewBranchFromHere = 10,
        NewTag = 11
    }

    private enum HistoryResetContextAction
    {
        Soft = 1,
        Mixed = 2,
        Hard = 3
    }

    private enum FilesContextAction
    {
        ShowDiff = 1,
        CompareWithWorkingTree = 2,
        EditSource = 3,
        RevertSelectedChanges = 4,
        CherryPickSelectedChanges = 5,
        CreatePatch = 6
    }

    private sealed class GitCommitTreeFileNode
    {
        public required GitCommitChangedFile File { get; init; }
        public required GitCommitFileDiffRequest DiffRequest { get; init; }
        public required GitCommitWorkingTreeDiffRequest WorkingTreeDiffRequest { get; init; }
        public required string AbsolutePath { get; init; }
    }

    private const float MinimumFilesHeight = 80f;
    private const float MinimumDetailsHeight = 72f;
    private const float HistoryCellHorizontalPadding = 6f;
    private const float GraphLeftPadding = 8f;
    private const float LaneWidth = 12f;
    private const float BadgeSpacing = 6f;
    private const float GraphLineThickness = 1.5f;
    private const float CommitDotRadius = 3.4f;
    private static readonly Color DeletedFileColor = new(1f, 0.45f, 0.38f);

    private static readonly Color[] LaneColors =
    [
        new("d38f5d"),
        new("7fa3d8"),
        new("69ad76"),
        new("c285d1"),
        new("d77b80"),
        new("c0a057")
    ];
    private static readonly FontVariation LocalAuthorBoldFont = ResourceLoader.Load<FontVariation>("res://Features/Git/Resources/InterBoldFontVariation.tres");

    private TabBar _tabsBar = null!;
    private Button _newTabButton = null!;
    private Label _statusLabel = null!;
    private Button _refreshButton = null!;
    private HSplitContainer _contentRoot = null!;
    private VSplitContainer _rightSplit = null!;
    private Label _emptyStateLabel = null!;
    private Tree _refsTree = null!;
    private LineEdit _searchLineEdit = null!;
    private Tree _historyTree = null!;
    private Tree _filesTree = null!;
    private RichTextLabel _messageLabel = null!;
    private RichTextLabel _shaLabel = null!;
    private RichTextLabel _authorLabel = null!;
    private RichTextLabel _dateLabel = null!;
    private RichTextLabel _friendlyDateLabel = null!;
    private RichTextLabel _parentsLabel = null!;

    private readonly Callable _historySubjectDrawCallable;
    private readonly Callable _historyAuthorDrawCallable;
    private readonly Callable _historyTimestampDrawCallable;
    private readonly Callable _refDrawCallable;
    private SharpIdeSolutionModel? _solution;
    private string _repoRootPath = string.Empty;
    private string _currentRefName = string.Empty;
    private string _currentBranchRefName = string.Empty;
    private string? _selectedCommitSha;
    private bool _gitCliAvailable;
    private bool _isDetachedHead;
    private bool _isLoadingHistory;
    private bool _suppressRefSelection;
    private bool _hasMoreHistory;
    private bool _suppressSearchChanged;
    private bool _suppressTabChanged;
    private int _lastTabIndex;
    private readonly List<GitHistoryRow> _historyRows = [];
    private readonly List<GitTreeTabState> _tabs = [];
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _refreshDebounceCts;
    private DateTimeOffset _suppressRepositoryRefreshUntil = DateTimeOffset.MinValue;

    [Inject] private readonly GitService _gitService = null!;
    [Inject] private readonly GitRepositoryMonitor _gitRepositoryMonitor = null!;
    [Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;

    public GitPanel()
    {
        _historySubjectDrawCallable = new Callable(this, MethodName.HistorySubjectCustomDraw);
        _historyAuthorDrawCallable = new Callable(this, MethodName.HistoryAuthorCustomDraw);
        _historyTimestampDrawCallable = new Callable(this, MethodName.HistoryTimestampCustomDraw);
        _refDrawCallable = new Callable(this, MethodName.RefCustomDraw);
    }

    public override void _Ready()
    {
        _tabsBar = GetNode<TabBar>("%TabsBar");
        _newTabButton = GetNode<Button>("%NewTabButton");
        _statusLabel = GetNode<Label>("%StatusLabel");
        _refreshButton = GetNode<Button>("%RefreshButton");
        _contentRoot = GetNode<HSplitContainer>("%ContentRoot");
        _rightSplit = GetNode<VSplitContainer>("%RightSplit");
        _emptyStateLabel = GetNode<Label>("%EmptyStateLabel");
        _refsTree = GetNode<Tree>("%RefsTree");
        _searchLineEdit = GetNode<LineEdit>("%SearchLineEdit");
        _historyTree = GetNode<Tree>("%HistoryTree");
        _filesTree = GetNode<Tree>("%FilesTree");
        _messageLabel = GetNode<RichTextLabel>("%MessageLabel");
        _shaLabel = GetNode<RichTextLabel>("%ShaLabel");
        _authorLabel = GetNode<RichTextLabel>("%AuthorLabel");
        _dateLabel = GetNode<RichTextLabel>("%DateLabel");
        _friendlyDateLabel = GetNode<RichTextLabel>("%FriendlyDateLabel");
        _parentsLabel = GetNode<RichTextLabel>("%ParentsLabel");

        ConfigureTrees();
        ConfigureTabs();

        _refreshButton.Pressed += () => _ = Task.GodotRun(RefreshAsync);
        _newTabButton.Pressed += OpenCurrentViewInNewTab;
        _refsTree.ItemSelected += OnRefsTreeItemSelected;
        _refsTree.ItemActivated += OnRefsTreeItemActivated;
        _refsTree.GuiInput += OnRefsTreeGuiInput;
        _searchLineEdit.TextChanged += OnSearchTextChanged;
        _historyTree.ItemSelected += OnHistoryTreeItemSelected;
        _historyTree.GuiInput += OnHistoryTreeGuiInput;
        _filesTree.ItemActivated += OnFilesTreeItemActivated;
        _filesTree.GuiInput += OnFilesTreeGuiInput;

        _gitRepositoryMonitor.RepositoryChanged.Subscribe(OnRepositoryChanged);
        SetProcess(true);
        _ = Task.GodotRun(AsyncReady);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationResized)
        {
            ClampRightPaneLayout();
        }
    }

    public override void _ExitTree()
    {
        _searchDebounceCts?.Cancel();
        _refreshDebounceCts?.Cancel();
        _gitRepositoryMonitor.RepositoryChanged.Unsubscribe(OnRepositoryChanged);
        _gitRepositoryMonitor.Stop();
    }

    private void ConfigureTrees()
    {
        _refsTree.HideRoot = true;
        _refsTree.Columns = 1;
        _refsTree.SelectMode = Tree.SelectModeEnum.Row;

        _historyTree.HideRoot = true;
        _historyTree.Columns = 3;
        _historyTree.SelectMode = Tree.SelectModeEnum.Multi;
        _historyTree.ColumnTitlesVisible = false;
        _historyTree.SetColumnExpand(0, true);
        _historyTree.SetColumnExpand(1, false);
        _historyTree.SetColumnExpand(2, false);
        _historyTree.SetColumnCustomMinimumWidth(1, 170);
        _historyTree.SetColumnCustomMinimumWidth(2, 150);

        _filesTree.HideRoot = true;
        _filesTree.Columns = 2;
        _filesTree.SelectMode = Tree.SelectModeEnum.Multi;
        _filesTree.ColumnTitlesVisible = false;
        _filesTree.SetColumnExpand(0, true);
        _filesTree.SetColumnExpand(1, false);
        _filesTree.SetColumnCustomMinimumWidth(1, 50);
    }

    private void ConfigureTabs()
    {
        _tabsBar.TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowAlways;
        _tabsBar.TabChanged += OnTabChanged;
        _tabsBar.TabClosePressed += OnTabClosePressed;
        _tabsBar.TabClicked += OnTabClicked;

        _tabs.Clear();
        _tabs.Add(new GitTreeTabState
        {
            Title = "Git",
            RefName = string.Empty,
            SearchText = string.Empty,
            SelectedCommitSha = null,
            IsMain = true
        });
        RebuildTabsBar();
    }

    private void ClampRightPaneLayout()
    {
        if (!IsNodeReady()) return;

        var rightHeight = Math.Max(0f, _rightSplit.Size.Y);
        if (rightHeight > 0f)
        {
            var maxFilesHeight = Math.Max(MinimumFilesHeight, rightHeight - MinimumDetailsHeight);
            var filesPanel = _rightSplit.GetChildOrNull<Control>(0);
            if (filesPanel is null)
            {
                return;
            }

            var currentFilesHeight = filesPanel.Size.Y;
            if (maxFilesHeight < MinimumFilesHeight)
            {
                SetSplitOffsetDelta(_rightSplit, Math.Max(0f, rightHeight * 0.45f) - currentFilesHeight);
            }
            else
            {
                SetSplitOffsetDelta(_rightSplit, Math.Clamp(currentFilesHeight, MinimumFilesHeight, maxFilesHeight) - currentFilesHeight);
            }
        }
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

    private static void SetSplitOffsetDelta(SplitContainer splitContainer, float delta)
    {
        if (Mathf.IsZeroApprox(delta))
        {
            return;
        }

        var offsets = splitContainer.SplitOffsets;
        var currentOffset = offsets.Length > 0 ? offsets[0] : 0;
        SetSplitOffset(splitContainer, currentOffset + (int)Mathf.Round(delta));
    }

    private void RebuildTabsBar()
    {
        _suppressTabChanged = true;
        try
        {
            while (_tabsBar.TabCount > 0)
            {
                _tabsBar.RemoveTab(_tabsBar.TabCount - 1);
            }

            foreach (var tab in _tabs)
            {
                _tabsBar.AddTab(tab.Title);
            }

            for (var index = 0; index < _tabs.Count; index++)
            {
                var title = _tabs[index].IsMain ? "Git" : _tabs[index].Title;
                _tabsBar.SetTabTitle(index, title);
            }

            _tabsBar.CurrentTab = Mathf.Clamp(_tabsBar.CurrentTab < 0 ? 0 : _tabsBar.CurrentTab, 0, Math.Max(0, _tabs.Count - 1));
            _lastTabIndex = _tabsBar.CurrentTab;
        }
        finally
        {
            _suppressTabChanged = false;
        }
    }

    private int ActiveTabIndex => Math.Clamp(_tabsBar.CurrentTab, 0, Math.Max(0, _tabs.Count - 1));

    private GitTreeTabState ActiveTab => _tabs[ActiveTabIndex];

    private void SaveTabState(int tabIndex)
    {
        if (_tabs.Count is 0 || tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var tab = _tabs[tabIndex];
        tab.RefName = tab.IsMain ? string.Empty : _currentRefName;
        tab.SearchText = _searchLineEdit.Text;
        tab.SelectedCommitSha = _selectedCommitSha;
        if (!tab.IsMain)
        {
            tab.Title = BuildTabTitle(tab.RefName, tab.SearchText);
            _tabsBar.SetTabTitle(tabIndex, tab.Title);
        }
    }

    private async Task LoadActiveTabAsync(bool forceReload = true)
    {
        if (_tabs.Count is 0) return;

        GitTreeTabState activeTab = null!;
        await this.InvokeAsync(() =>
        {
            activeTab = ActiveTab;
            _suppressSearchChanged = true;
            try
            {
                _searchLineEdit.Text = activeTab.SearchText;
            }
            finally
            {
                _suppressSearchChanged = false;
            }

            _currentRefName = activeTab.RefName;
            _selectedCommitSha = activeTab.SelectedCommitSha;
            if (activeTab.IsMain)
            {
                _refsTree.DeselectAll();
            }
            else if (!string.IsNullOrWhiteSpace(_currentRefName))
            {
                SelectRefTreeItem(_currentRefName);
            }
        });

        if (forceReload)
        {
            await ReloadHistoryAsync(reset: true);
        }
    }

    private void OpenCurrentViewInNewTab()
    {
        if (ActiveTab.IsMain || string.IsNullOrWhiteSpace(_currentRefName))
        {
            return;
        }

        SaveTabState(ActiveTabIndex);
        var newTab = new GitTreeTabState
        {
            Title = BuildTabTitle(_currentRefName, _searchLineEdit.Text),
            RefName = _currentRefName,
            SearchText = _searchLineEdit.Text,
            SelectedCommitSha = _selectedCommitSha,
            IsMain = false
        };
        _tabs.Add(newTab);
        RebuildTabsBar();
        _tabsBar.CurrentTab = _tabs.Count - 1;
    }

    private async void OnTabChanged(long tab)
    {
        if (_suppressTabChanged) return;
        SaveTabState(_lastTabIndex);
        _lastTabIndex = (int)tab;
        await LoadActiveTabAsync(forceReload: true);
    }

    private async void OnTabClicked(long tab)
    {
        if (_suppressTabChanged) return;
        if (tab != _tabsBar.CurrentTab)
        {
            _tabsBar.CurrentTab = (int)tab;
            return;
        }

        await LoadActiveTabAsync(forceReload: true);
    }

    private void OnTabClosePressed(long tab)
    {
        if (tab <= 0 || tab >= _tabs.Count) return;

        _tabs.RemoveAt((int)tab);
        var nextIndex = Math.Clamp((int)tab - 1, 0, Math.Max(0, _tabs.Count - 1));
        RebuildTabsBar();
        _tabsBar.CurrentTab = nextIndex;
        _lastTabIndex = nextIndex;
        _ = Task.GodotRun(() => LoadActiveTabAsync(forceReload: true));
    }

    private static string BuildTabTitle(string refName, string searchText)
    {
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            return string.IsNullOrWhiteSpace(refName)
                ? searchText
                : $"{GetShortRefName(refName)} · {searchText}";
        }

        return string.IsNullOrWhiteSpace(refName) ? "Git" : GetShortRefName(refName);
    }

    private static string GetShortRefName(string refName)
    {
        return refName
            .Replace("refs/heads/", string.Empty, StringComparison.Ordinal)
            .Replace("refs/remotes/", string.Empty, StringComparison.Ordinal)
            .Replace("refs/tags/", string.Empty, StringComparison.Ordinal);
    }

    private async Task AsyncReady()
    {
        await _solutionAccessor.SolutionReadyTcs.Task;
        _solution = _solutionAccessor.SolutionModel;
        _gitCliAvailable = await _gitService.IsGitCliAvailable();
        await RefreshAsync();
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
            await Task.Delay(300, _refreshDebounceCts.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync()
    {
        if (_solution is null)
        {
            return;
        }

        var snapshot = await _gitService.GetSnapshot(_solution.FilePath, commitCount: 1);
        if (!snapshot.Repository.IsRepositoryDiscovered)
        {
            _repoRootPath = string.Empty;
            _gitRepositoryMonitor.Stop();
            await this.InvokeAsync(() => ShowEmptyState("No git repository was found for the current solution."));
            return;
        }

        _repoRootPath = snapshot.Repository.RepoRootPath;
        _isDetachedHead = snapshot.Repository.IsDetachedHead;
        await this.InvokeAsync(() => _statusLabel.Text = snapshot.Repository.BranchDisplayName);
        _gitRepositoryMonitor.Start(snapshot.Repository.RepoRootPath, snapshot.Repository.GitDirectoryPath);

        if (!_gitCliAvailable)
        {
            await this.InvokeAsync(() => ShowEmptyState("The git tree browser requires the `git` executable to be available on PATH."));
            return;
        }

        var refs = await _gitService.GetRepositoryRefs(_solution.FilePath);
        _currentBranchRefName = refs.FirstOrDefault(node => node.Kind == GitRefKind.Head)?.RefName ?? string.Empty;
        await this.InvokeAsync(() =>
        {
            _contentRoot.Visible = true;
            _emptyStateLabel.Visible = false;
            PopulateRefsTree(refs);
            ClampRightPaneLayout();
        });

        await LoadActiveTabAsync(forceReload: true);
    }

    private void ShowEmptyState(string message)
    {
        _contentRoot.Visible = false;
        _emptyStateLabel.Visible = true;
        _emptyStateLabel.Text = message;
        _statusLabel.Text = "No repository";
        _historyRows.Clear();
        _currentRefName = string.Empty;
        _currentBranchRefName = string.Empty;
        _isDetachedHead = false;
        ClearDetails();
    }

    private void PopulateRefsTree(IReadOnlyList<GitRefNode> refs)
    {
        _refsTree.Clear();
        var root = _refsTree.CreateItem();
        foreach (var node in refs)
        {
            CreateRefItem(root, node);
        }
    }

    private void CreateRefItem(TreeItem parent, GitRefNode node)
    {
        var item = _refsTree.CreateItem(parent);
        item.SetMetadata(0, new RefCountedContainer(node));
        item.SetCellMode(0, TreeItem.TreeCellMode.Custom);
        item.SetCustomAsButton(0, true);
        item.SetCustomDrawCallback(0, _refDrawCallable);
        item.Collapsed = node.DisplayName switch
        {
            "Remote" => true,
            "Tags" => true,
            _ => false
        };

        foreach (var child in node.Children)
        {
            CreateRefItem(item, child);
        }
    }

    private void SelectRefTreeItem(string refName)
    {
        var root = _refsTree.GetRoot();
        if (root is null) return;

        var target = FindRefItemRecursive(root, refName);
        if (target is null) return;

        ExpandParents(target);
        _suppressRefSelection = true;
        try
        {
            _refsTree.SetSelected(target, 0);
            target.Select(0);
        }
        finally
        {
            _suppressRefSelection = false;
        }
    }

    private static TreeItem? FindRefItemRecursive(TreeItem item, string refName)
    {
        if (item.GetTypedMetadata<GitRefNode>(0)?.RefName == refName)
        {
            return item;
        }

        for (var child = item.GetFirstChild(); child is not null; child = child.GetNext())
        {
            var found = FindRefItemRecursive(child, refName);
            if (found is not null) return found;
        }

        return null;
    }

    private static void ExpandParents(TreeItem item)
    {
        for (var current = item.GetParent(); current is not null; current = current.GetParent())
        {
            current.Collapsed = false;
        }
    }

    private void OnRefsTreeItemSelected()
    {
        if (_suppressRefSelection) return;
        var selected = _refsTree.GetSelected();
        var node = selected?.GetTypedMetadata<GitRefNode>(0);
        if (node is null || !node.IsSelectable || string.IsNullOrWhiteSpace(node.RefName)) return;
        OpenOrFocusScopedTab(node.RefName);
    }

    private void OnRefsTreeItemActivated()
    {
        var selected = _refsTree.GetSelected();
        var node = selected?.GetTypedMetadata<GitRefNode>(0);
        if (node is null || !node.IsSelectable || string.IsNullOrWhiteSpace(node.RefName)) return;

        OpenOrFocusScopedTab(node.RefName);
    }

    private void OnRefsTreeGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouseEvent) return;

        _suppressRefSelection = true;
        var selected = SelectTreeItemAtPosition(_refsTree, mouseEvent.Position);
        _suppressRefSelection = false;
        var node = selected?.GetTypedMetadata<GitRefNode>(0);
        if (node is null || !node.IsSelectable || string.IsNullOrWhiteSpace(node.RefName) || node.Kind is GitRefKind.Head)
        {
            return;
        }

        OpenRefContextMenu(node);
        _refsTree.AcceptEvent();
    }

    private void OpenRefContextMenu(GitRefNode node)
    {
        var menu = new PopupMenu();
        AddChild(menu);

        switch (node.Kind)
        {
            case GitRefKind.LocalBranch:
                PopulateLocalBranchContextMenu(menu, node);
                break;
            case GitRefKind.RemoteBranch:
                PopulateRemoteBranchContextMenu(menu, node);
                break;
            case GitRefKind.Tag:
                PopulateTagContextMenu(menu, node);
                break;
            default:
                menu.QueueFree();
                return;
        }

        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id => _ = Task.GodotRun(() => HandleRefContextActionAsync(node, (RefContextAction)id));
        PopupMenuAtMouse(menu);
    }

    private void PopulateLocalBranchContextMenu(PopupMenu menu, GitRefNode node)
    {
        var shortName = node.ShortName ?? GetShortRefName(node.RefName ?? node.DisplayName);
        if (!node.IsCurrent)
        {
            menu.AddItem("Checkout", (int)RefContextAction.Checkout);
        }
        menu.AddItem($"New branch from '{shortName}'", (int)RefContextAction.NewBranchFrom);
        
        menu.AddSeparator();
        menu.AddItem($"Show Diff '{shortName}' with working tree", (int)RefContextAction.ShowDiffWithWorkingTree);
        if (!node.IsCurrent && !_isDetachedHead)
        {
            menu.AddItem($"Show Diff current branch with '{shortName}'", (int)RefContextAction.ShowDiffCurrentBranchWithSelected);
        }

        if (!node.IsCurrent && !_isDetachedHead)
        {
            menu.AddSeparator();
            menu.AddItem($"Rebase current branch onto '{shortName}'", (int)RefContextAction.RebaseCurrentOntoSelected);
            menu.AddItem($"Merge '{shortName}' into current branch", (int)RefContextAction.MergeSelectedIntoCurrent);
        }

        if (node.IsCurrent)
        {
            menu.AddSeparator();
            menu.AddItem("Update", (int)RefContextAction.UpdateCurrentBranch);
            menu.AddItem("Push", (int)RefContextAction.PushCurrentBranch);
        }

        var addedRenameDeleteGroup = false;
        if (string.IsNullOrWhiteSpace(node.UpstreamRefName))
        {
            menu.AddSeparator();
            menu.AddItem("Rename", (int)RefContextAction.Rename);
            addedRenameDeleteGroup = true;
        }

        if (!node.IsCurrent)
        {
            if (!addedRenameDeleteGroup)
            {
                menu.AddSeparator();
            }

            menu.AddItem("Delete", (int)RefContextAction.DeleteLocalBranch);
        }
    }

    private void PopulateRemoteBranchContextMenu(PopupMenu menu, GitRefNode node)
    {
        var shortName = node.ShortName ?? GetShortRefName(node.RefName ?? node.DisplayName);
        menu.AddItem("Checkout", (int)RefContextAction.Checkout);
        menu.AddItem($"New branch from '{shortName}'", (int)RefContextAction.NewBranchFrom);

        menu.AddSeparator();
        menu.AddItem($"Show Diff '{shortName}' with working tree", (int)RefContextAction.ShowDiffWithWorkingTree);
        if (!_isDetachedHead)
        {
            menu.AddItem($"Show Diff current branch with '{shortName}'", (int)RefContextAction.ShowDiffCurrentBranchWithSelected);
        }
    }

    private void PopulateTagContextMenu(PopupMenu menu, GitRefNode node)
    {
        var shortName = node.ShortName ?? GetShortRefName(node.RefName ?? node.DisplayName);
        menu.AddItem("Checkout", (int)RefContextAction.Checkout);
        menu.AddSeparator();
        if (!_isDetachedHead)
        {
            menu.AddItem($"Compare current branch with tag '{shortName}'", (int)RefContextAction.ShowDiffCurrentBranchWithSelected);
        }

        menu.AddItem($"Compare tag '{shortName}' with working tree", (int)RefContextAction.ShowDiffWithWorkingTree);

        menu.AddSeparator();
        menu.AddItem("Push", (int)RefContextAction.PushTag);

        menu.AddSeparator();
        menu.AddItem("Delete locally", (int)RefContextAction.DeleteLocalTag);
        if (node.ExistsOnPreferredRemote)
        {
            menu.AddItem("Delete remotely", (int)RefContextAction.DeleteRemoteTag);
        }
    }

    private async Task HandleRefContextActionAsync(GitRefNode node, RefContextAction action)
    {
        if (string.IsNullOrWhiteSpace(node.RefName))
        {
            return;
        }

        switch (action)
        {
            case RefContextAction.Checkout:
                await RunRefActionAsync(() => _gitService.CheckoutRef(_repoRootPath, node.RefName));
                break;
            case RefContextAction.NewBranchFrom:
            {
                var initialName = node.Kind is GitRefKind.RemoteBranch
                    ? GetSuggestedBranchNameForRemote(node)
                    : node.ShortName ?? GetShortRefName(node.RefName);
                var branchName = await PromptForTextAsync("New Branch", "Branch name", initialName);
                if (string.IsNullOrWhiteSpace(branchName)) return;
                await RunRefActionAsync(() => _gitService.CreateBranchFromRef(_repoRootPath, node.RefName, branchName));
                break;
            }
            case RefContextAction.ShowDiffWithWorkingTree:
                // TODO
                break;
            case RefContextAction.ShowDiffCurrentBranchWithSelected:
                //TODO
                break;
            case RefContextAction.RebaseCurrentOntoSelected:
                if (await ConfirmAsync("Rebase Branch", $"Rebase current branch onto '{node.ShortName ?? GetShortRefName(node.RefName)}'?"))
                {
                    await RunRefActionAsync(() => _gitService.RebaseCurrentBranchOnto(_repoRootPath, node.RefName));
                }
                break;
            case RefContextAction.MergeSelectedIntoCurrent:
                if (await ConfirmAsync("Merge Branch", $"Merge '{node.ShortName ?? GetShortRefName(node.RefName)}' into the current branch?"))
                {
                    await RunRefActionAsync(() => _gitService.MergeRefIntoCurrent(_repoRootPath, node.RefName));
                }
                break;
            case RefContextAction.UpdateCurrentBranch:
                await RunRefActionAsync(() => _gitService.UpdateCurrentBranch(_repoRootPath));
                break;
            case RefContextAction.PushCurrentBranch:
                await RunRefActionAsync(() => _gitService.PushCurrentBranch(_repoRootPath));
                break;
            case RefContextAction.Rename:
            {
                var newName = await PromptForTextAsync("Rename Branch", "Branch name", node.ShortName ?? GetShortRefName(node.RefName));
                if (string.IsNullOrWhiteSpace(newName)) return;
                await RunRefActionAsync(() => _gitService.RenameLocalBranch(_repoRootPath, node.RefName, newName));
                break;
            }
            case RefContextAction.DeleteLocalBranch:
                if (await ConfirmAsync("Delete Branch", $"Delete local branch '{node.ShortName ?? GetShortRefName(node.RefName)}'?"))
                {
                    await RunRefActionAsync(() => _gitService.DeleteLocalBranch(_repoRootPath, node.RefName));
                }
                break;
            case RefContextAction.PushTag:
                await RunRefActionAsync(() => _gitService.PushTag(_repoRootPath, node.RefName, node.PreferredRemoteName));
                break;
            case RefContextAction.DeleteLocalTag:
                if (await ConfirmAsync("Delete Tag", $"Delete local tag '{node.ShortName ?? GetShortRefName(node.RefName)}'?"))
                {
                    await RunRefActionAsync(() => _gitService.DeleteLocalTag(_repoRootPath, node.RefName));
                }
                break;
            case RefContextAction.DeleteRemoteTag:
                if (await ConfirmAsync("Delete Remote Tag", $"Delete remote tag '{node.ShortName ?? GetShortRefName(node.RefName)}' from '{node.PreferredRemoteName}'?"))
                {
                    await RunRefActionAsync(() => _gitService.DeleteRemoteTag(_repoRootPath, node.RefName, node.PreferredRemoteName));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task RunRefActionAsync(Func<Task> action)
    {
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
            _suppressRepositoryRefreshUntil = DateTimeOffset.UtcNow.AddMilliseconds(700);
            await RefreshAsync();
        }
    }

    private void OpenOrFocusScopedTab(string refName)
    {
        SaveTabState(ActiveTabIndex);

        var existingTabIndex = _tabs.FindIndex(tab =>
            !tab.IsMain &&
            string.Equals(tab.RefName, refName, StringComparison.Ordinal));
        if (existingTabIndex >= 0)
        {
            _tabsBar.CurrentTab = existingTabIndex;
            return;
        }

        _tabs.Add(new GitTreeTabState
        {
            Title = BuildTabTitle(refName, string.Empty),
            RefName = refName,
            SearchText = string.Empty,
            SelectedCommitSha = null,
            IsMain = false
        });
        RebuildTabsBar();
        _tabsBar.CurrentTab = _tabs.Count - 1;
    }

    public override void _Process(double delta)
    {
        OnHistoryScrolled();
    }

    private void OnSearchTextChanged(string newText)
    {
        if (_suppressSearchChanged) return;
        ActiveTab.SearchText = newText;
        if (!ActiveTab.IsMain)
        {
            ActiveTab.Title = BuildTabTitle(_currentRefName, newText);
            _tabsBar.SetTabTitle(ActiveTabIndex, ActiveTab.Title);
        }
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;
        _ = Task.GodotRun(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                await ReloadHistoryAsync(reset: true);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task ReloadHistoryAsync(bool reset)
    {
        if (_isLoadingHistory || string.IsNullOrWhiteSpace(_repoRootPath))
        {
            return;
        }

        if (!ActiveTab.IsMain && string.IsNullOrWhiteSpace(_currentRefName))
        {
            return;
        }

        _isLoadingHistory = true;
        try
        {
            var searchText = await this.InvokeAsync(() => _searchLineEdit.Text.Trim());
            var (mode, term) = GetSearch(searchText);
            var page = await _gitService.GetHistoryPage(
                _repoRootPath,
                new GitHistoryQuery
                {
                    IncludeAllRefs = ActiveTab.IsMain,
                    RefName = ActiveTab.IsMain ? null : _currentRefName,
                    SearchMode = mode,
                    SearchTerm = term,
                    Skip = reset ? 0 : _historyRows.Count,
                    Take = 200
                });

            await this.InvokeAsync(() =>
            {
                if (reset)
                {
                    _historyRows.Clear();
                }

                _historyRows.AddRange(page.Rows);
                _hasMoreHistory = page.HasMore;
                PopulateHistoryTree();
                if (_historyRows.Count is 0)
                {
                    ClearDetails();
                }
                else if (reset)
                {
                    SelectHistoryRow(_selectedCommitSha);
                }
            });
        }
        finally
        {
            _isLoadingHistory = false;
        }
    }

    private static (GitHistorySearchMode Mode, string Term) GetSearch(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return (GitHistorySearchMode.None, string.Empty);
        }

        return rawText.StartsWith('@')
            ? (GitHistorySearchMode.Paths, rawText[1..].Trim())
            : (GitHistorySearchMode.CommitMetadata, rawText);
    }

    private void PopulateHistoryTree()
    {
        _historyTree.Clear();
        var root = _historyTree.CreateItem();
        if (_historyRows.Count is 0)
        {
            var emptyItem = _historyTree.CreateItem(root);
            emptyItem.SetText(0, "No commits matched the current selection.");
            return;
        }

        for (var rowIndex = 0; rowIndex < _historyRows.Count; rowIndex++)
        {
            var row = _historyRows[rowIndex];
            var item = _historyTree.CreateItem(root);
            item.SetMetadata(0, new RefCountedContainer(row));
            item.SetMetadata(1, rowIndex);
            item.SetCellMode(0, TreeItem.TreeCellMode.Custom);
            item.SetCellMode(1, TreeItem.TreeCellMode.Custom);
            item.SetCellMode(2, TreeItem.TreeCellMode.Custom);
            item.SetCustomAsButton(0, true);
            item.SetCustomAsButton(1, true);
            item.SetCustomAsButton(2, true);
            item.SetCustomDrawCallback(0, _historySubjectDrawCallable);
            item.SetCustomDrawCallback(1, _historyAuthorDrawCallable);
            item.SetCustomDrawCallback(2, _historyTimestampDrawCallable);
            item.SetTooltipText(0, $"{row.ShortSha} {row.Subject}");
            item.SetTooltipText(1, row.AuthorEmail);
            item.SetTooltipText(2, row.CommittedAt.LocalDateTime.ToString("F"));
        }
    }

    private void SelectHistoryRow(string? commitSha)
    {
        var root = _historyTree.GetRoot();
        TreeItem? target = null;
        if (!string.IsNullOrWhiteSpace(commitSha) && root is not null)
        {
            for (var child = root.GetFirstChild(); child is not null; child = child.GetNext())
            {
                var row = child.GetTypedMetadata<GitHistoryRow>(0);
                if (row is null || !string.Equals(row.Sha, commitSha, StringComparison.Ordinal)) continue;
                target = child;
                break;
            }
        }

        if (target is null && root is not null)
        {
            for (var child = root.GetFirstChild(); child is not null; child = child.GetNext())
            {
                var row = child.GetTypedMetadata<GitHistoryRow>(0);
                if (row is null) continue;
                target = child;
                break;
            }
        }

        if (target is null) return;
        _historyTree.SetSelected(target, 0);
        target.Select(0);
        OnHistoryTreeItemSelected();
    }

    private void OnHistoryTreeItemSelected()
    {
        var selected = _historyTree.GetSelected();
        var row = selected?.GetTypedMetadata<GitHistoryRow>(0);
        if (row is null) return;
        var requestedCommitSha = row.Sha;
        var requestedRepoRootPath = _repoRootPath;
        _selectedCommitSha = requestedCommitSha;
        ActiveTab.SelectedCommitSha = requestedCommitSha;

        _ = Task.GodotRun(async () =>
        {
            var detailsTask = _gitService.GetCommitDetails(requestedRepoRootPath, requestedCommitSha);
            var filesTask = _gitService.GetCommitChangedFiles(requestedRepoRootPath, requestedCommitSha);
            await Task.WhenAll(detailsTask, filesTask);
            var details = await detailsTask;
            var files = await filesTask;
            await this.InvokeAsync(() =>
            {
                if (!string.Equals(_repoRootPath, requestedRepoRootPath, StringComparison.Ordinal)
                    || !string.Equals(_selectedCommitSha, requestedCommitSha, StringComparison.Ordinal))
                {
                    return;
                }

                PopulateFilesTree(requestedCommitSha, files);
                PopulateDetails(details);
            });
        });
    }

    private void OnHistoryTreeGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouseEvent)
        {
            return;
        }

        var selected = SelectContextTreeItemAtPosition(_historyTree, mouseEvent.Position);
        var row = selected?.GetTypedMetadata<GitHistoryRow>(0);
        if (row is null)
        {
            return;
        }

        _historyTree.AcceptEvent();
        _ = Task.GodotRun(OpenHistoryContextMenuAsync);
    }

    private async Task OpenHistoryContextMenuAsync()
    {
        var selectedRows = GetSelectedHistoryRows();
        if (selectedRows.Count is 0)
        {
            return;
        }

        GitCommitCapabilities? capabilities = null;
        if (selectedRows.Count is 1)
        {
            capabilities = await _gitService.GetCommitCapabilities(_repoRootPath, selectedRows[0].Sha);
        }

        await this.InvokeAsync(() => OpenHistoryContextMenu(selectedRows, capabilities));
    }

    private void OpenHistoryContextMenu(IReadOnlyList<GitHistoryRow> selectedRows, GitCommitCapabilities? capabilities)
    {
        if (selectedRows.Count is 0)
        {
            return;
        }

        var isSingle = selectedRows.Count is 1;
        var singleRow = selectedRows[0];
        var menu = new PopupMenu();
        AddChild(menu);

        menu.AddItem("Copy Revision Number", (int)HistoryContextAction.CopyRevisionNumber);
        menu.AddItem("Create Patch...", (int)HistoryContextAction.CreatePatch);
        menu.AddItem("Cherry-Pick", (int)HistoryContextAction.CherryPick);
        menu.AddSeparator();
        menu.AddItem("Checkout Revision", (int)HistoryContextAction.CheckoutRevision);
        menu.SetItemDisabled(menu.ItemCount - 1, !isSingle);
        menu.AddItem("Compare with Working Tree", (int)HistoryContextAction.CompareWithWorkingTree);
        menu.SetItemDisabled(menu.ItemCount - 1, true); // TODO: requires a dedicated compare surface.
        menu.AddSeparator();

        var resetSubmenu = new PopupMenu();
        menu.AddSubmenuNodeItem("Reset Current Branch to Here", resetSubmenu, (int)HistoryContextAction.ResetCurrentBranchToHere);
        var canReset = isSingle;
        menu.SetItemDisabled(menu.ItemCount - 1, !canReset);
        resetSubmenu.AddItem("Soft", (int)HistoryResetContextAction.Soft);
        resetSubmenu.SetItemDisabled(resetSubmenu.ItemCount - 1, !canReset);
        resetSubmenu.AddItem("Mixed", (int)HistoryResetContextAction.Mixed);
        resetSubmenu.SetItemDisabled(resetSubmenu.ItemCount - 1, !canReset);
        resetSubmenu.AddItem("Hard", (int)HistoryResetContextAction.Hard);
        resetSubmenu.SetItemDisabled(resetSubmenu.ItemCount - 1, !canReset);

        menu.AddItem("Revert Commit", (int)HistoryContextAction.RevertCommit);
        menu.AddItem("Undo Commit", (int)HistoryContextAction.UndoCommit);
        menu.SetItemDisabled(menu.ItemCount - 1, !(isSingle && capabilities?.CanUndoCommit == true));
        menu.AddItem("Edit Commit Message", (int)HistoryContextAction.EditCommitMessage);
        menu.SetItemDisabled(menu.ItemCount - 1, !(isSingle && capabilities?.CanEditMessage == true));
        menu.AddSeparator();
        menu.AddItem("New Branch from Here...", (int)HistoryContextAction.NewBranchFromHere);
        menu.SetItemDisabled(menu.ItemCount - 1, !isSingle);
        menu.AddItem("New Tag", (int)HistoryContextAction.NewTag);
        menu.SetItemDisabled(menu.ItemCount - 1, !isSingle);

        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id => _ = Task.GodotRun(() => HandleHistoryContextActionAsync(selectedRows, capabilities, (HistoryContextAction)id));
        resetSubmenu.IdPressed += id => _ = Task.GodotRun(() => HandleHistoryResetContextActionAsync(singleRow, (HistoryResetContextAction)id));
        PopupMenuAtMouse(menu);
    }

    private void PopulateFilesTree(string commitSha, IReadOnlyList<GitCommitChangedFile> files)
    {
        _filesTree.Clear();
        var root = _filesTree.CreateItem();
        if (files.Count is 0)
        {
            var emptyItem = _filesTree.CreateItem(root);
            emptyItem.SetText(0, "No file changes");
            return;
        }

        var directoryMap = new Dictionary<string, TreeItem>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root
        };

        foreach (var file in files.OrderBy(file => file.RepoRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(file.RepoRelativePath.Replace('/', Path.DirectorySeparatorChar))?
                .Replace(Path.DirectorySeparatorChar, '/')
                ?? string.Empty;
            var parent = EnsureDirectory(directoryMap, root, directory);
            var item = _filesTree.CreateItem(parent);
            var absolutePath = Path.Combine(_repoRootPath, file.RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            item.SetText(0, Path.GetFileName(file.RepoRelativePath));
            item.SetText(1, file.StatusCode);
            item.SetTooltipText(0, file.DisplayPath);
            ApplyCommitChangeItemStyles(item, file);
            item.SetMetadata(0, new RefCountedContainer(new GitCommitTreeFileNode
            {
                File = file,
                AbsolutePath = absolutePath,
                DiffRequest = new GitCommitFileDiffRequest
                {
                    RepoRootPath = _repoRootPath,
                    CommitSha = commitSha,
                    RepoRelativePath = file.RepoRelativePath,
                    OldRepoRelativePath = file.OldRepoRelativePath
                },
                WorkingTreeDiffRequest = new GitCommitWorkingTreeDiffRequest
                {
                    RepoRootPath = _repoRootPath,
                    CommitSha = commitSha,
                    RepoRelativePath = file.RepoRelativePath,
                    OldRepoRelativePath = file.OldRepoRelativePath,
                    StatusCode = file.StatusCode
                }
            }));
        }
    }

    private static TreeItem EnsureDirectory(Dictionary<string, TreeItem> directoryMap, TreeItem root, string directory)
    {
        if (directoryMap.TryGetValue(directory, out var existing))
        {
            return existing;
        }

        var parentPath = Path.GetDirectoryName(directory.Replace('/', Path.DirectorySeparatorChar))?
            .Replace(Path.DirectorySeparatorChar, '/')
            ?? string.Empty;
        var parent = EnsureDirectory(directoryMap, root, parentPath);
        var created = root.GetTree().CreateItem(parent);
        created.SetText(0, Path.GetFileName(directory));
        created.Collapsed = false;
        directoryMap[directory] = created;
        return created;
    }

    private void PopulateDetails(GitCommitDetails details)
    {
        _messageLabel.Text = FormatCommitMessage(details);
        _shaLabel.Text = FormatDetailsLine("Hash:", details.Sha);
        _authorLabel.Text = FormatDetailsLine("Author:", $"{details.AuthorName} <{details.AuthorEmail}>");
        _dateLabel.Text = FormatDetailsLine("Date:", details.CommittedAt.LocalDateTime.ToString("F"));
        _friendlyDateLabel.Text = FormatDetailsLine("When:", details.FriendlyCommittedTimestamp);
        _parentsLabel.Text = FormatDetailsLine(
            details.ParentShas.Count is 1 ? "Parent:" : "Parents:",
            details.ParentShas.Count switch
        {
            0 => "root commit",
            1 => details.ParentShas[0],
            _ => string.Join(", ", details.ParentShas)
        });
    }

    private void ClearDetails()
    {
        _filesTree.Clear();
        _filesTree.CreateItem();
        _messageLabel.Text = string.Empty;
        _shaLabel.Text = FormatDetailsLine("Hash:", string.Empty);
        _authorLabel.Text = FormatDetailsLine("Author:", string.Empty);
        _dateLabel.Text = FormatDetailsLine("Date:", string.Empty);
        _friendlyDateLabel.Text = FormatDetailsLine("When:", string.Empty);
        _parentsLabel.Text = FormatDetailsLine("Parents:", string.Empty);
    }

    private static void ApplyCommitChangeItemStyles(TreeItem item, GitCommitChangedFile file)
    {
        var color = GetCommitChangeColor(file.StatusCode);
        item.SetCustomColor(0, color);
        item.SetCustomColor(1, color);
    }

    private static Color GetCommitChangeColor(string statusCode)
    {
        return statusCode[..Math.Min(1, statusCode.Length)] switch
        {
            "A" => GitColours.GitNewFileColour,
            "D" => DeletedFileColor,
            _ => GitColours.GitEditedFileColour
        };
    }

    private static string FormatCommitMessage(GitCommitDetails details)
    {
        var subject = EscapeBbCode(details.Subject);
        var messageBody = details.FullMessage.StartsWith(details.Subject, StringComparison.Ordinal)
            ? details.FullMessage[details.Subject.Length..].TrimStart('\r', '\n')
            : details.FullMessage;
        return string.IsNullOrWhiteSpace(messageBody)
            ? $"[b]{subject}[/b]"
            : $"[b]{subject}[/b]\n\n{EscapeBbCode(messageBody)}";
    }

    private static string FormatDetailsLine(string label, string value)
    {
        var formattedLabel = $"[color=#93a0b5][b]{EscapeBbCode(label)}[/b][/color]";
        return string.IsNullOrWhiteSpace(value)
            ? formattedLabel
            : $"{formattedLabel} {EscapeBbCode(value)}";
    }

    private static string EscapeBbCode(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("[", "[lb]").Replace("]", "[rb]");
    }

    private void OnFilesTreeItemActivated()
    {
        var selected = _filesTree.GetSelected();
        var fileNode = selected?.GetTypedMetadata<GitCommitTreeFileNode>(0);
        if (fileNode is null) return;
        GodotGlobalEvents.Instance.GitCommitDiffRequested.InvokeParallelFireAndForget(fileNode.DiffRequest);
    }

    private void OnFilesTreeGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouseEvent)
        {
            return;
        }

        var selected = SelectContextTreeItemAtPosition(_filesTree, mouseEvent.Position);
        var fileNode = selected?.GetTypedMetadata<GitCommitTreeFileNode>(0);
        if (fileNode is null)
        {
            return;
        }

        OpenFilesContextMenu(GetSelectedCommitFileNodes());
        _filesTree.AcceptEvent();
    }

    private void OpenFilesContextMenu(IReadOnlyList<GitCommitTreeFileNode> selectedFiles)
    {
        if (selectedFiles.Count is 0)
        {
            return;
        }

        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("Show Diff", (int)FilesContextAction.ShowDiff);
        menu.AddItem("Compare with Working Tree", (int)FilesContextAction.CompareWithWorkingTree);
        menu.AddItem("Edit Source", (int)FilesContextAction.EditSource);
        menu.SetItemDisabled(menu.ItemCount - 1, !selectedFiles.Any(file => File.Exists(file.AbsolutePath)));
        menu.AddSeparator();
        menu.AddItem("Revert Selected Changes", (int)FilesContextAction.RevertSelectedChanges);
        menu.AddItem("Cherry-Pick Selected Changes", (int)FilesContextAction.CherryPickSelectedChanges);
        menu.AddItem("Create Patch...", (int)FilesContextAction.CreatePatch);
        menu.PopupHide += () => menu.QueueFree();
        menu.IdPressed += id => _ = Task.GodotRun(() => HandleFilesContextActionAsync(selectedFiles, (FilesContextAction)id));
        PopupMenuAtMouse(menu);
    }

    private async Task HandleHistoryContextActionAsync(
        IReadOnlyList<GitHistoryRow> selectedRows,
        GitCommitCapabilities? capabilities,
        HistoryContextAction action)
    {
        if (selectedRows.Count is 0)
        {
            return;
        }

        var singleRow = selectedRows[0];
        switch (action)
        {
            case HistoryContextAction.CopyRevisionNumber:
                DisplayServer.ClipboardSet(string.Join('\n', selectedRows.Select(row => row.Sha)));
                break;
            case HistoryContextAction.CreatePatch:
            {
                var patchText = await _gitService.BuildCommitPatchText(_repoRootPath, selectedRows.Reverse().Select(row => row.Sha).ToList());
                var suggestedFileName = selectedRows.Count is 1
                    ? $"{singleRow.ShortSha}.patch"
                    : $"{selectedRows[^1].ShortSha}-{selectedRows[0].ShortSha}.patch";
                var savePath = await PromptForSavePathAsync("Create Patch", suggestedFileName);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    return;
                }

                await File.WriteAllTextAsync(savePath, patchText);
                break;
            }
            case HistoryContextAction.CherryPick:
                if (!await ConfirmAsync("Cherry-Pick Commit", BuildCommitSelectionMessage("Cherry-pick", selectedRows)))
                {
                    return;
                }

                await RunPanelGitActionAsync(async () =>
                {
                    foreach (var row in selectedRows.Reverse())
                    {
                        await _gitService.CherryPickCommit(_repoRootPath, row.Sha);
                    }
                });
                break;
            case HistoryContextAction.CheckoutRevision:
                if (selectedRows.Count is not 1)
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.CheckoutCommit(_repoRootPath, singleRow.Sha));
                break;
            case HistoryContextAction.CompareWithWorkingTree:
                break;
            case HistoryContextAction.ResetCurrentBranchToHere:
                break;
            case HistoryContextAction.RevertCommit:
                if (!await ConfirmAsync("Revert Commit", BuildCommitSelectionMessage("Revert", selectedRows)))
                {
                    return;
                }

                await RunPanelGitActionAsync(async () =>
                {
                    foreach (var row in selectedRows)
                    {
                        await _gitService.RevertCommit(_repoRootPath, row.Sha);
                    }
                });
                break;
            case HistoryContextAction.UndoCommit:
                if (selectedRows.Count is not 1 || capabilities?.CanUndoCommit != true)
                {
                    return;
                }

                if (!await ConfirmAsync("Undo Commit", $"Undo latest commit '{singleRow.Subject}' and keep its changes staged?"))
                {
                    return;
                }

                var details = await _gitService.GetCommitDetails(_repoRootPath, singleRow.Sha);
                if (details.ParentShas.Count is 0)
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.Reset(_repoRootPath, details.ParentShas[0], ResetMode.Soft));
                break;
            case HistoryContextAction.EditCommitMessage:
                if (selectedRows.Count is not 1 || capabilities?.CanEditMessage != true)
                {
                    return;
                }

                var commitDetails = await _gitService.GetCommitDetails(_repoRootPath, singleRow.Sha);
                var newMessage = await PromptForCommitMessageAsync("Edit Commit Message", commitDetails.FullMessage);
                if (string.IsNullOrWhiteSpace(newMessage))
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.EditCommitMessage(_repoRootPath, singleRow.Sha, newMessage));
                break;
            case HistoryContextAction.NewBranchFromHere:
            {
                if (selectedRows.Count is not 1)
                {
                    return;
                }

                var branchName = await PromptForTextAsync("New Branch", "Branch name", $"branch-from-{singleRow.ShortSha}");
                if (string.IsNullOrWhiteSpace(branchName))
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.CreateBranchFromCommit(_repoRootPath, singleRow.Sha, branchName));
                break;
            }
            case HistoryContextAction.NewTag:
            {
                if (selectedRows.Count is not 1)
                {
                    return;
                }

                var tagName = await PromptForTextAsync("New Tag", "Tag name", singleRow.ShortSha);
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.CreateTagAtCommit(_repoRootPath, singleRow.Sha, tagName));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task HandleHistoryResetContextActionAsync(GitHistoryRow row, HistoryResetContextAction action)
    {
        var mode = action switch
        {
            HistoryResetContextAction.Soft => ResetMode.Soft,
            HistoryResetContextAction.Mixed => ResetMode.Mixed,
            HistoryResetContextAction.Hard => ResetMode.Hard,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
        var confirmationMessage = mode is ResetMode.Hard
            ? $"Hard reset the current branch to '{row.ShortSha}'? This will discard working tree and index changes."
            : $"Reset the current branch to '{row.ShortSha}' using {mode.ToString().ToLowerInvariant()} mode?";
        if (!await ConfirmAsync("Reset Branch", confirmationMessage))
        {
            return;
        }

        await RunPanelGitActionAsync(() => _gitService.Reset(_repoRootPath, row.Sha, mode));
    }

    private async Task HandleFilesContextActionAsync(IReadOnlyList<GitCommitTreeFileNode> selectedFiles, FilesContextAction action)
    {
        if (selectedFiles.Count is 0)
        {
            return;
        }

        var commitSha = selectedFiles[0].DiffRequest.CommitSha;
        var selectedPaths = selectedFiles.Select(file => file.File.RepoRelativePath).ToList();
        switch (action)
        {
            case FilesContextAction.ShowDiff:
                foreach (var file in selectedFiles)
                {
                    GodotGlobalEvents.Instance.GitCommitDiffRequested.InvokeParallelFireAndForget(file.DiffRequest);
                }
                break;
            case FilesContextAction.CompareWithWorkingTree:
                foreach (var file in selectedFiles)
                {
                    GodotGlobalEvents.Instance.GitCommitWorkingTreeDiffRequested.InvokeParallelFireAndForget(file.WorkingTreeDiffRequest);
                }
                break;
            case FilesContextAction.EditSource:
                foreach (var file in selectedFiles.Where(file => File.Exists(file.AbsolutePath)))
                {
                    OpenSourceFile(file.AbsolutePath);
                }
                break;
            case FilesContextAction.RevertSelectedChanges:
                if (!await ConfirmAsync("Revert Selected Changes", BuildFileSelectionMessage("Revert", selectedFiles)))
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.ApplyCommitFiles(
                    _repoRootPath,
                    commitSha,
                    selectedPaths,
                    GitHistoricalFileApplyMode.Revert));
                break;
            case FilesContextAction.CherryPickSelectedChanges:
                if (!await ConfirmAsync("Cherry-Pick Selected Changes", BuildFileSelectionMessage("Cherry-pick", selectedFiles)))
                {
                    return;
                }

                await RunPanelGitActionAsync(() => _gitService.ApplyCommitFiles(
                    _repoRootPath,
                    commitSha,
                    selectedPaths,
                    GitHistoricalFileApplyMode.CherryPick));
                break;
            case FilesContextAction.CreatePatch:
            {
                var patchText = await _gitService.BuildCommitFilesPatchText(_repoRootPath, commitSha, selectedPaths);
                var suggestedFileName = selectedFiles.Count is 1
                    ? $"{selectedFiles[0].File.RepoRelativePath.Replace('/', '_')}.patch"
                    : $"{selectedFiles[0].DiffRequest.CommitSha[..Math.Min(8, selectedFiles[0].DiffRequest.CommitSha.Length)]}-files.patch";
                var savePath = await PromptForSavePathAsync("Create Patch", suggestedFileName);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    return;
                }

                await File.WriteAllTextAsync(savePath, patchText);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ConfirmationDialog
        {
            Title = title,
            DialogText = message
        };
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.InvokeAsync(() =>
        {
            AddChild(dialog);
            dialog.Confirmed += () => tcs.TrySetResult(true);
            dialog.Canceled += () => tcs.TrySetResult(false);
            dialog.CloseRequested += () => tcs.TrySetResult(false);
            dialog.PopupCentered();
        });

        var result = await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
        return result;
    }

    private async Task<string?> PromptForTextAsync(string title, string label, string initialText)
    {
        var dialog = new ConfirmationDialog
        {
            Title = title
        };
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.OffsetLeft = 8;
        margin.OffsetTop = 8;
        margin.OffsetRight = -8;
        margin.OffsetBottom = -52;
        var vbox = new VBoxContainer();
        var labelControl = new Label { Text = label };
        var lineEdit = new LineEdit { Text = initialText };
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.InvokeAsync(() =>
        {
            vbox.AddChild(labelControl);
            vbox.AddChild(lineEdit);
            margin.AddChild(vbox);
            dialog.AddChild(margin);
            AddChild(dialog);
            dialog.Confirmed += () => tcs.TrySetResult(lineEdit.Text.Trim());
            dialog.Canceled += () => tcs.TrySetResult(null);
            dialog.CloseRequested += () => tcs.TrySetResult(null);
            dialog.PopupCentered(new Vector2I(420, 0));
            lineEdit.GrabFocus();
            lineEdit.SelectAll();
        });

        var result = await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
        return result;
    }

    private async Task<string?> PromptForCommitMessageAsync(string title, string initialText)
    {
        var dialog = new ConfirmationDialog
        {
            Title = title
        };
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.OffsetLeft = 8;
        margin.OffsetTop = 8;
        margin.OffsetRight = -8;
        margin.OffsetBottom = -52;
        var vbox = new VBoxContainer();
        var label = new Label { Text = "Commit message" };
        var textEdit = new TextEdit
        {
            Text = initialText,
            CustomMinimumSize = new Vector2(480, 160),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.InvokeAsync(() =>
        {
            vbox.AddChild(label);
            vbox.AddChild(textEdit);
            margin.AddChild(vbox);
            dialog.AddChild(margin);
            AddChild(dialog);
            dialog.Confirmed += () => tcs.TrySetResult(textEdit.Text.Trim());
            dialog.Canceled += () => tcs.TrySetResult(null);
            dialog.CloseRequested += () => tcs.TrySetResult(null);
            dialog.PopupCentered(new Vector2I(520, 260));
            textEdit.GrabFocus();
        });

        var result = await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
        return result;
    }

    private async Task<string?> PromptForSavePathAsync(string title, string suggestedFileName)
    {
        var dialog = new FileDialog
        {
            Title = title,
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            CurrentFile = suggestedFileName,
            Filters = ["*.patch ; Patch Files"]
        };
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.InvokeAsync(() =>
        {
            AddChild(dialog);
            dialog.FileSelected += path => tcs.TrySetResult(path);
            dialog.Canceled += () => tcs.TrySetResult(null);
            dialog.CloseRequested += () => tcs.TrySetResult(null);
            dialog.PopupCentered(new Vector2I(720, 520));
        });

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
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.InvokeAsync(() =>
        {
            AddChild(dialog);
            dialog.Confirmed += () => tcs.TrySetResult();
            dialog.CloseRequested += () => tcs.TrySetResult();
            dialog.PopupCentered();
        });
        await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
    }

    private async Task RunPanelGitActionAsync(Func<Task> action)
    {
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
            _suppressRepositoryRefreshUntil = DateTimeOffset.UtcNow.AddMilliseconds(700);
            await RefreshAsync();
        }
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

    private static TreeItem? SelectContextTreeItemAtPosition(Tree tree, Vector2 mousePosition)
    {
        var item = tree.GetItemAtPosition(mousePosition);
        if (item is null)
        {
            return null;
        }

        if (!item.IsSelected(0))
        {
            tree.DeselectAll();
            tree.SetSelected(item, 0);
            item.Select(0);
        }

        return item;
    }

    private List<GitHistoryRow> GetSelectedHistoryRows()
    {
        return GetSelectedTreeItems(_historyTree)
            .Select(item => item.GetTypedMetadata<GitHistoryRow>(0))
            .OfType<GitHistoryRow>()
            .ToList();
    }

    private List<GitCommitTreeFileNode> GetSelectedCommitFileNodes()
    {
        return GetSelectedTreeItems(_filesTree)
            .Select(item => item.GetTypedMetadata<GitCommitTreeFileNode>(0))
            .OfType<GitCommitTreeFileNode>()
            .ToList();
    }

    private static List<TreeItem> GetSelectedTreeItems(Tree tree)
    {
        var items = new List<TreeItem>();
        var current = tree.GetNextSelected(null);
        while (current is not null)
        {
            items.Add(current);
            current = tree.GetNextSelected(current);
        }

        return items;
    }

    private static string BuildCommitSelectionMessage(string verb, IReadOnlyList<GitHistoryRow> rows)
    {
        return rows.Count is 1
            ? $"{verb} commit '{rows[0].Subject}'?"
            : $"{verb} {rows.Count} selected commits?";
    }

    private static string BuildFileSelectionMessage(string verb, IReadOnlyList<GitCommitTreeFileNode> files)
    {
        return files.Count is 1
            ? $"{verb} changes from '{files[0].File.DisplayPath}'?"
            : $"{verb} changes for {files.Count} selected files?";
    }

    private bool TryResolveSolutionFile(string absolutePath, out SharpIdeFile file)
    {
        file = null!;
        if (_solution is null)
        {
            return false;
        }

        if (_solution.AllFiles.GetValueOrDefault(absolutePath) is { } exactFile)
        {
            file = exactFile;
            return true;
        }

        var normalizedPath = Path.GetFullPath(absolutePath);
        var matchedFile = _solution.AllFiles.Values.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (matchedFile is null)
        {
            return false;
        }

        file = matchedFile;
        return true;
    }

    private void OpenSourceFile(string absolutePath)
    {
        if (TryResolveSolutionFile(absolutePath, out var file))
        {
            GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(file, null);
            return;
        }

        if (File.Exists(absolutePath))
        {
            GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(SharpIdeFile.CreateStandalone(absolutePath), null);
        }
    }

    private static string GetSuggestedBranchNameForRemote(GitRefNode node)
    {
        if (string.IsNullOrWhiteSpace(node.RefName))
        {
            return node.ShortName ?? string.Empty;
        }

        var friendlyName = GetShortRefName(node.RefName);
        var separatorIndex = friendlyName.IndexOf('/');
        return separatorIndex >= 0 && separatorIndex < friendlyName.Length - 1
            ? friendlyName[(separatorIndex + 1)..]
            : friendlyName;
    }

    private void OnHistoryScrolled()
    {
        if (!_hasMoreHistory || _isLoadingHistory) return;
        var lastItem = GetLastChild(_historyTree.GetRoot());
        if (lastItem is null) return;
        var lastItemRect = _historyTree.GetItemAreaRect(lastItem);
        if (lastItemRect.Position.Y > _historyTree.Size.Y + 120f) return;
        _ = Task.GodotRun(() => ReloadHistoryAsync(reset: false));
    }

    private static TreeItem? GetLastChild(TreeItem? item)
    {
        if (item is null) return null;
        var child = item.GetFirstChild();
        TreeItem? last = null;
        while (child is not null)
        {
            last = child;
            child = child.GetNext();
        }

        return last;
    }

    private void RefCustomDraw(TreeItem treeItem, Rect2 rect)
    {
        var node = treeItem.GetTypedMetadata<GitRefNode>(0);
        if (node is null) return;
        var textColor = ResolveTextColor(_refsTree, treeItem);
        var font = _refsTree.GetThemeFont(ThemeStringNames.Font);
        var fontSize = _refsTree.GetThemeFontSize(ThemeStringNames.FontSize);
        var y = rect.Position.Y + (rect.Size.Y + fontSize) / 2 - 2f;
        var availableWidth = Math.Max(0f, rect.Size.X - HistoryCellHorizontalPadding * 2f);
        var label = EllipsizeText(font, node.DisplayName, fontSize, availableWidth);
        _refsTree.DrawString(font, new Vector2(rect.Position.X + HistoryCellHorizontalPadding, y), label, HorizontalAlignment.Left, availableWidth, fontSize, textColor);
    }

    private void HistorySubjectCustomDraw(TreeItem treeItem, Rect2 rect)
    {
        var row = treeItem.GetTypedMetadata<GitHistoryRow>(0);
        if (row is null) return;

        var textColor = ResolveHistoryRowTextColor(treeItem, row);
        var subtleColor = new Color(textColor, 0.72f);
        var font = _historyTree.GetThemeFont(ThemeStringNames.Font);
        var fontSize = _historyTree.GetThemeFontSize(ThemeStringNames.FontSize);
        var centerY = rect.Position.Y + rect.Size.Y * 0.5f;
        var graphStartX = rect.Position.X + GraphLeftPadding;
        var maxGraphColumn = row.CommitLaneIndex;
        foreach (var segment in row.GraphSegments)
        {
            maxGraphColumn = Math.Max(maxGraphColumn, Math.Max(segment.FromColumnIndex, segment.ToColumnIndex));
            var color = ResolveHistoryGraphColor(row, LaneColors[Math.Abs(segment.ColorIndex) % LaneColors.Length]);
            DrawHistoryGraphSegment(rect, graphStartX, segment, color, row);
        }

        var commitColor = ResolveHistoryGraphColor(row, LaneColors[Math.Abs(row.CommitColorIndex) % LaneColors.Length]);
        _historyTree.DrawCircle(new Vector2(graphStartX + row.CommitLaneIndex * LaneWidth, centerY), CommitDotRadius, commitColor);

        var graphWidth = Math.Max(22f, (maxGraphColumn + 1) * LaneWidth + 6f);
        var textX = rect.Position.X + graphWidth;
        var textY = rect.Position.Y + (rect.Size.Y + fontSize) / 2 - 2f;
        var availableWidth = Math.Max(0f, rect.End.X - textX - HistoryCellHorizontalPadding);
        if (availableWidth <= 0f)
        {
            return;
        }

        var badgeFontSize = Math.Max(9, fontSize - 2);
        var badgeBudget = row.Decorations.Count is 0 ? 0f : availableWidth * 0.4f;
        var subjectText = EllipsizeText(font, row.Subject, fontSize, badgeBudget > 0f ? Math.Max(0f, availableWidth - badgeBudget) : availableWidth);
        var subjectWidth = font.GetStringSize(subjectText, HorizontalAlignment.Left, -1, fontSize).X;
        _historyTree.DrawString(font, new Vector2(textX, textY), subjectText, HorizontalAlignment.Left, availableWidth, fontSize, textColor);

        var badgeX = textX + subjectWidth + 8f;
        var remainingBadgeWidth = rect.End.X - HistoryCellHorizontalPadding - badgeX;
        foreach (var decoration in row.Decorations)
        {
            if (remainingBadgeWidth <= 18f)
            {
                break;
            }

            var badgeText = EllipsizeText(font, decoration, badgeFontSize, remainingBadgeWidth - 10f);
            if (string.IsNullOrEmpty(badgeText))
            {
                break;
            }

            var badgeTextWidth = font.GetStringSize(badgeText, HorizontalAlignment.Left, -1, badgeFontSize).X;
            var badgeRect = new Rect2(badgeX, rect.Position.Y + 4f, badgeTextWidth + 10f, rect.Size.Y - 8f);
            if (badgeRect.End.X > rect.End.X - HistoryCellHorizontalPadding)
            {
                break;
            }

            _historyTree.DrawRect(badgeRect, new Color("243344"));
            _historyTree.DrawRect(badgeRect, new Color("52789f"), false, 1f);
            _historyTree.DrawString(font, new Vector2(badgeRect.Position.X + 5f, textY), badgeText, HorizontalAlignment.Left, -1, badgeFontSize, subtleColor);
            badgeX = badgeRect.End.X + BadgeSpacing;
            remainingBadgeWidth = rect.End.X - HistoryCellHorizontalPadding - badgeX;
        }
    }

    private void HistoryAuthorCustomDraw(TreeItem treeItem, Rect2 rect)
    {
        var row = treeItem.GetTypedMetadata<GitHistoryRow>(0);
        if (row is null) return;

        var textColor = ResolveHistoryRowTextColor(treeItem, row);
        var font = row.IsLocalAuthor && LocalAuthorBoldFont is not null
            ? (Font)LocalAuthorBoldFont
            : _historyTree.GetThemeFont(ThemeStringNames.Font);
        var fontSize = _historyTree.GetThemeFontSize(ThemeStringNames.FontSize);
        var textY = rect.Position.Y + (rect.Size.Y + fontSize) / 2 - 2f;
        var availableWidth = Math.Max(0f, rect.Size.X - HistoryCellHorizontalPadding * 2f);
        var authorName = EllipsizeText(font, row.AuthorName, fontSize, availableWidth);
        _historyTree.DrawString(font, new Vector2(rect.Position.X + HistoryCellHorizontalPadding, textY), authorName, HorizontalAlignment.Left, availableWidth, fontSize, textColor);
    }

    private void HistoryTimestampCustomDraw(TreeItem treeItem, Rect2 rect)
    {
        var row = treeItem.GetTypedMetadata<GitHistoryRow>(0);
        if (row is null) return;

        var textColor = new Color(ResolveHistoryRowTextColor(treeItem, row), 0.78f);
        var font = _historyTree.GetThemeFont(ThemeStringNames.Font);
        var fontSize = _historyTree.GetThemeFontSize(ThemeStringNames.FontSize);
        var textY = rect.Position.Y + (rect.Size.Y + fontSize) / 2 - 2f;
        var availableWidth = Math.Max(0f, rect.Size.X - HistoryCellHorizontalPadding * 2f);
        var timestamp = EllipsizeText(font, row.FriendlyCommittedTimestamp, fontSize, availableWidth);
        _historyTree.DrawString(font, new Vector2(rect.Position.X + HistoryCellHorizontalPadding, textY), timestamp, HorizontalAlignment.Right, availableWidth, fontSize, textColor);
    }

    private static Color ResolveTextColor(Tree tree, TreeItem treeItem)
    {
        var hovered = tree.GetItemAtPosition(tree.GetLocalMousePosition()) == treeItem;
        var isSelected = treeItem.IsSelected(0);
        return (isSelected, hovered) switch
        {
            (true, true) => tree.GetThemeColor(ThemeStringNames.FontHoveredSelectedColor),
            (true, false) => tree.GetThemeColor(ThemeStringNames.FontSelectedColor),
            (false, true) => tree.GetThemeColor(ThemeStringNames.FontHoveredColor),
            _ => tree.GetThemeColor(ThemeStringNames.FontColor)
        };
    }

    private Color ResolveHistoryRowTextColor(TreeItem treeItem, GitHistoryRow row)
    {
        var textColor = ResolveTextColor(_historyTree, treeItem);
        if (!ActiveTab.IsMain && !row.IsPrimaryBranchCommit)
        {
            return new Color(textColor, 0.52f);
        }

        return textColor;
    }

    private static GitGraphCell? GetGraphCell(GitHistoryRow? row, int columnIndex)
    {
        if (row is null)
        {
            return null;
        }

        return row.GraphCells.FirstOrDefault(cell => cell.ColumnIndex == columnIndex);
    }

    private static char GetGraphChar(GitHistoryRow? row, int columnIndex)
    {
        if (row is null || columnIndex < 0 || columnIndex >= row.GraphPrefix.Length)
        {
            return ' ';
        }

        return row.GraphPrefix[columnIndex];
    }

    private static bool HasVerticalContinuation(GitGraphCell? graphCell)
    {
        return graphCell?.Kind is GitGraphCellKind.Vertical or GitGraphCellKind.Commit or GitGraphCellKind.SlashUp or GitGraphCellKind.SlashDown;
    }

    private static Color ResolveLaneColor(GitGraphCell? currentCell, GitGraphCell? previousCell, GitGraphCell? nextCell, int columnIndex)
    {
        var colorIndex = currentCell?.ColorIndex ?? previousCell?.ColorIndex ?? nextCell?.ColorIndex ?? Math.Abs(columnIndex / 2);
        return LaneColors[Math.Abs(colorIndex) % LaneColors.Length];
    }

    private Color ResolveHistoryGraphColor(GitHistoryRow row, Color baseColor)
    {
        if (!ActiveTab.IsMain && !row.IsPrimaryBranchCommit)
        {
            return new Color(baseColor, 0.45f);
        }

        return baseColor;
    }

    private void DrawHistoryGraphSegment(Rect2 rect, float graphStartX, GitGraphSegment segment, Color color, GitHistoryRow row)
    {
        var fromX = graphStartX + segment.FromColumnIndex * LaneWidth;
        var fromY = GetGraphAnchorY(segment.FromAnchor, rect);
        var toX = graphStartX + segment.ToColumnIndex * LaneWidth;
        var toY = GetGraphAnchorY(segment.ToAnchor, rect);

        if (Mathf.IsEqualApprox(fromX, toX))
        {
            if (segment.FromColumnIndex == row.CommitLaneIndex && segment.ToColumnIndex == row.CommitLaneIndex)
            {
                fromY = AdjustGraphCenterTouchingEndpoint(segment.FromAnchor, segment.ToAnchor, rect);
                toY = AdjustGraphCenterTouchingEndpoint(segment.ToAnchor, segment.FromAnchor, rect);
            }
            else if (IsContinuousStraightLane(row, segment))
            {
                if (segment.FromAnchor != GitGraphAnchor.Top || segment.ToAnchor != GitGraphAnchor.Center)
                {
                    return;
                }

                fromY = GetGraphAnchorY(GitGraphAnchor.Top, rect);
                toY = GetGraphAnchorY(GitGraphAnchor.Bottom, rect);
            }
            else if (segment.FromAnchor == GitGraphAnchor.Center && segment.ToAnchor == GitGraphAnchor.Bottom)
            {
                return;
            }

            _historyTree.DrawLine(new Vector2(fromX, fromY), new Vector2(toX, toY), color, GraphLineThickness);
            return;
        }

        _historyTree.DrawLine(new Vector2(fromX, fromY), new Vector2(toX, toY), color, GraphLineThickness);
    }

    private static bool IsContinuousStraightLane(GitHistoryRow row, GitGraphSegment segment)
    {
        if (segment.FromColumnIndex != segment.ToColumnIndex)
        {
            return false;
        }

        return row.GraphSegments.Any(candidate =>
            candidate.FromColumnIndex == segment.FromColumnIndex &&
            candidate.ToColumnIndex == segment.ToColumnIndex &&
            candidate.FromAnchor == GitGraphAnchor.Top &&
            candidate.ToAnchor == GitGraphAnchor.Center) &&
            row.GraphSegments.Any(candidate =>
                candidate.FromColumnIndex == segment.FromColumnIndex &&
                candidate.ToColumnIndex == segment.ToColumnIndex &&
                candidate.FromAnchor == GitGraphAnchor.Center &&
                candidate.ToAnchor == GitGraphAnchor.Bottom);
    }

    private static float AdjustGraphCenterTouchingEndpoint(GitGraphAnchor anchor, GitGraphAnchor oppositeAnchor, Rect2 rect)
    {
        if (anchor != GitGraphAnchor.Center)
        {
            return GetGraphAnchorY(anchor, rect);
        }

        var centerY = rect.Position.Y + rect.Size.Y * 0.5f;
        return oppositeAnchor == GitGraphAnchor.Top
            ? centerY - CommitDotRadius - 0.75f
            : centerY + CommitDotRadius + 0.75f;
    }

    private static float GetGraphAnchorY(GitGraphAnchor anchor, Rect2 rect)
    {
        return anchor switch
        {
            GitGraphAnchor.Top => rect.Position.Y,
            GitGraphAnchor.Center => rect.Position.Y + rect.Size.Y * 0.5f,
            GitGraphAnchor.Bottom => rect.End.Y,
            _ => rect.Position.Y + rect.Size.Y * 0.5f
        };
    }

    private static string EllipsizeText(Font font, string text, int fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
        {
            return string.Empty;
        }

        if (font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X <= maxWidth)
        {
            return text;
        }

        const string suffix = "...";
        if (font.GetStringSize(suffix, HorizontalAlignment.Left, -1, fontSize).X > maxWidth)
        {
            return string.Empty;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = text[..mid].TrimEnd() + suffix;
            if (font.GetStringSize(candidate, HorizontalAlignment.Left, -1, fontSize).X <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return text[..low].TrimEnd() + suffix;
    }

    private sealed class GitTreeTabState
    {
        public required string Title { get; set; }
        public required string RefName { get; set; }
        public required string SearchText { get; set; }
        public string? SelectedCommitSha { get; set; }
        public required bool IsMain { get; init; }
    }
}
