using Godot;
using SharpIDE.Application;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.Git;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.CodeEditor;

namespace SharpIDE.Godot.Features.Git;

public partial class GitDiffViewer : MarginContainer
{
    private static readonly PackedScene SharpIdeCodeEditScene = GD.Load<PackedScene>("res://Features/CodeEditor/SharpIdeCodeEdit.tscn");

    private Button _previousChangeButton = null!;
    private Button _nextChangeButton = null!;
    private Button _editSourceButton = null!;
    private Button _previousDiffButton = null!;
    private Button _nextDiffButton = null!;
    private Button _saveButton = null!;
    private Label _summaryLabel = null!;
    private Label _emptyLabel = null!;
    private Control _diffViewportHost = null!;
    private HBoxContainer _diffEditorRow = null!;
    private GitDiffConnectorOverlay _connectorOverlay = null!;
    private PanelContainer _baseEditorPanel = null!;
    private PanelContainer _actionGutterPanel = null!;
    private PanelContainer _currentEditorPanel = null!;
    private Label _baseEditorLabel = null!;
    private Label _currentEditorLabel = null!;
    private VScrollBar _baseExternalScrollBar = null!;
    private GitChangeScrollbarOverlay _baseScrollbarOverlay = null!;
    private MarginContainer _baseEditorContentHost = null!;
    private Control _actionGutterSpacer = null!;
    private GitDiffActionGutter _actionGutter = null!;
    private MarginContainer _baseEditorHost = null!;
    private MarginContainer _currentEditorHost = null!;
    private HSplitContainer _mergeSplit = null!;
    private CodeEdit _localCodeEdit = null!;
    private CodeEdit _currentCodeEdit = null!;
    private CodeEdit _incomingCodeEdit = null!;
    private SharpIdeCodeEditContainer? _baseEditorContainer;
    private SharpIdeCodeEditContainer? _currentDiffEditorContainer;
    private SharpIdeCodeEdit? _baseEditor;
    private SharpIdeCodeEdit? _currentDiffEditor;

    private readonly GitDiffSessionState _session = new();
    private bool _isRefreshing;
    private bool _isRunningGitAction;
    private bool _suppressCurrentEditorChanged;
    private bool _syncingScroll;
    private bool _syncingHorizontalScroll;
    private bool _syncingBaseExternalScroll;
    private bool _suppressLinkedScrollSync;
    private bool _isHistoricalReadOnly;
    private bool _listenForRepositoryChanges;
    private bool _pendingRefreshRequested;
    private bool _pendingScrollToFirstChange;
    private CancellationTokenSource? _repoRefreshDebounceCts;
    private CancellationTokenSource? _editRefreshDebounceCts;
    private DateTimeOffset _suppressRepositoryRefreshUntil = DateTimeOffset.MinValue;
    private IReadOnlyList<string> _adjacentDiffPaths = [];
    private int _currentDiffPathIndex = -1;
    private GitCommitFileDiffRequest? _historicalCommitDiffRequest;
    private GitStashFileDiffRequest? _historicalStashDiffRequest;

    [Inject] private readonly GitService _gitService = null!;
    [Inject] private readonly GitRepositoryMonitor _gitRepositoryMonitor = null!;
    [Inject] private readonly FileChangedService _fileChangedService = null!;
    [Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;

    public string SourcePath { get; private set; } = string.Empty;
    public string PreviewKey { get; private set; } = string.Empty;
    public GitFileContentViewModel? CurrentView { get; private set; }

    public override void _Ready()
    {
        _previousChangeButton = GetNode<Button>("%PreviousChangeButton");
        _nextChangeButton = GetNode<Button>("%NextChangeButton");
        _editSourceButton = GetNode<Button>("%EditSourceButton");
        _previousDiffButton = GetNode<Button>("%PreviousDiffButton");
        _nextDiffButton = GetNode<Button>("%NextDiffButton");
        _saveButton = GetNode<Button>("%SaveButton");
        _summaryLabel = GetNode<Label>("%SummaryLabel");
        _emptyLabel = GetNode<Label>("%EmptyLabel");
        _diffViewportHost = GetNode<Control>("%DiffViewportHost");
        _diffEditorRow = GetNode<HBoxContainer>("%DiffEditorRow");
        _connectorOverlay = GetNode<GitDiffConnectorOverlay>("%ConnectorOverlay");
        _baseEditorPanel = GetNode<PanelContainer>("%BaseEditorPanel");
        _actionGutterPanel = GetNode<PanelContainer>("%ActionGutterPanel");
        _currentEditorPanel = GetNode<PanelContainer>("%CurrentEditorPanel");
        _baseEditorLabel = GetNode<Label>("%BaseEditorLabel");
        _currentEditorLabel = GetNode<Label>("%CurrentEditorLabel");
        _baseExternalScrollBar = GetNode<VScrollBar>("%BaseExternalScrollBar");
        _baseScrollbarOverlay = GetNode<GitChangeScrollbarOverlay>("%BaseScrollbarOverlay");
        _baseEditorContentHost = GetNode<MarginContainer>("%BaseEditorContentHost");
        _actionGutterSpacer = GetNode<Control>("%ActionGutterSpacer");
        _actionGutter = GetNode<GitDiffActionGutter>("%ActionGutter");
        _baseEditorHost = GetNode<MarginContainer>("%BaseEditorHost");
        _currentEditorHost = GetNode<MarginContainer>("%CurrentEditorHost");
        _mergeSplit = GetNode<HSplitContainer>("%MergeSplit");
        _localCodeEdit = GetNode<CodeEdit>("%LocalCodeEdit");
        _currentCodeEdit = GetNode<CodeEdit>("%CurrentCodeEdit");
        _incomingCodeEdit = GetNode<CodeEdit>("%IncomingCodeEdit");

        _previousChangeButton.Pressed += () => NavigateToChange(forward: false);
        _nextChangeButton.Pressed += () => NavigateToChange(forward: true);
        _editSourceButton.Pressed += () => _ = OpenSourceFileAsync();
        _previousDiffButton.Pressed += () => _ = NavigateToAdjacentDiffAsync(forward: false);
        _nextDiffButton.Pressed += () => _ = NavigateToAdjacentDiffAsync(forward: true);
        _saveButton.Pressed += () => _ = SaveCurrentMergeTextAsync();
        _baseExternalScrollBar.ValueChanged += OnBaseExternalScrollChanged;
        _actionGutter.StageLinesRequested += ids => _ = StageLinesAsync(ids);
        _actionGutter.UnstageLinesRequested += ids => _ = UnstageLinesAsync(ids);
        _actionGutter.StageChunkRequested += chunkId => _ = StageChunkAsync(chunkId);
        _actionGutter.UnstageChunkRequested += chunkId => _ = UnstageChunkAsync(chunkId);
        _actionGutter.RevertChunkRequested += chunkId => _ = RevertChunkAsync(chunkId);
        _actionGutter.DividerDragged += OnDividerDragged;
        _gitRepositoryMonitor.RepositoryChanged.Subscribe(OnRepositoryChanged);
        _connectorOverlay.BindLayout(_diffViewportHost, _diffEditorRow, _actionGutter);
        ApplyViewerChrome();
    }

    public override void _ExitTree()
    {
        _repoRefreshDebounceCts?.Cancel();
        _editRefreshDebounceCts?.Cancel();
        _gitRepositoryMonitor.RepositoryChanged.Unsubscribe(OnRepositoryChanged);
        ClearDiffEditors();
    }

    public async Task LoadFromPath(string absolutePath)
    {
        _isHistoricalReadOnly = false;
        _historicalCommitDiffRequest = null;
        _historicalStashDiffRequest = null;
        var scrollToFirstChange = false;
        if (!string.Equals(SourcePath, absolutePath, StringComparison.Ordinal))
        {
            scrollToFirstChange = true;
            _session.HorizontalScroll = 0;
            _session.VerticalScroll = 0;
            _session.CaretLine = 0;
            _session.CaretColumn = 0;
        }

        SourcePath = absolutePath;
        PreviewKey = Path.GetFullPath(absolutePath);
        await EnsureRepositoryMonitoringAsync();
        await RefreshAsync(scrollToFirstChange);
    }

    public async Task LoadHistoricalDiff(GitCommitFileDiffRequest request)
    {
        _isHistoricalReadOnly = true;
        _historicalCommitDiffRequest = request;
        _historicalStashDiffRequest = null;
        SourcePath = Path.Combine(request.RepoRootPath, request.RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        PreviewKey = $"{request.CommitSha}:{request.RepoRelativePath}";
        _listenForRepositoryChanges = false;
        _session.HorizontalScroll = 0;
        _session.VerticalScroll = 0;
        _session.CaretLine = 0;
        _session.CaretColumn = 0;
        await RefreshAsync(scrollToFirstChange: true);
    }

    public async Task LoadHistoricalDiff(GitStashFileDiffRequest request)
    {
        _isHistoricalReadOnly = true;
        _historicalCommitDiffRequest = null;
        _historicalStashDiffRequest = request;
        SourcePath = Path.Combine(request.RepoRootPath, request.RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        PreviewKey = $"{request.StashRef}:{request.RepoRelativePath}";
        _listenForRepositoryChanges = false;
        _session.HorizontalScroll = 0;
        _session.VerticalScroll = 0;
        _session.CaretLine = 0;
        _session.CaretColumn = 0;
        await RefreshAsync(scrollToFirstChange: true);
    }

    private async Task EnsureRepositoryMonitoringAsync()
    {
        if (_isHistoricalReadOnly || string.IsNullOrWhiteSpace(SourcePath))
        {
            _listenForRepositoryChanges = false;
            return;
        }

        var snapshot = await _gitService.GetSnapshot(SourcePath, commitCount: 1);
        if (snapshot.Repository.IsRepositoryDiscovered)
        {
            _listenForRepositoryChanges = true;
            _gitRepositoryMonitor.Start(snapshot.Repository.RepoRootPath, snapshot.Repository.GitDirectoryPath);
        }
        else
        {
            _listenForRepositoryChanges = false;
        }
    }

    private async Task OnRepositoryChanged()
    {
        if (_isHistoricalReadOnly) return;
        if (!_listenForRepositoryChanges) return;
        if (string.IsNullOrWhiteSpace(SourcePath)) return;
        if (DateTimeOffset.UtcNow < _suppressRepositoryRefreshUntil) return;

        var previousRepoRefreshDebounceCts = _repoRefreshDebounceCts;
        _repoRefreshDebounceCts = new CancellationTokenSource();
        if (previousRepoRefreshDebounceCts is not null)
        {
            await previousRepoRefreshDebounceCts.CancelAsync();
            previousRepoRefreshDebounceCts.Dispose();
        }
        try
        {
            await Task.Delay(250, _repoRefreshDebounceCts.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync(bool scrollToFirstChange = false, GitDiffTraceOperation? traceOperation = null)
    {
        if (string.IsNullOrWhiteSpace(SourcePath)) return;
        if (_isRefreshing)
        {
            _pendingRefreshRequested = true;
            _pendingScrollToFirstChange |= scrollToFirstChange;
            return;
        }

        CaptureEditorViewportState();
        _isRefreshing = true;
        using var activity = traceOperation?.StartChild($"{nameof(GitDiffViewer)}.{nameof(RefreshAsync)}")
            ?? SharpIdeOtel.Source.StartActivity($"{nameof(GitDiffViewer)}.{nameof(RefreshAsync)}");
        activity?.SetTag("git.diff.scroll_to_first_change", scrollToFirstChange);
        activity?.SetTag("git.diff.source_path", SourcePath);
        try
        {
            if (_isHistoricalReadOnly)
            {
                GitDiffViewModel diffView;
                if (_historicalCommitDiffRequest is not null)
                {
                    diffView = await _gitService.GetCommitFileDiffView(_historicalCommitDiffRequest);
                }
                else if (_historicalStashDiffRequest is not null)
                {
                    diffView = await _gitService.GetStashFileDiffView(_historicalStashDiffRequest);
                }
                else
                {
                    return;
                }

                CurrentView = new GitFileContentViewModel
                {
                    Kind = GitFileContentViewKind.Diff,
                    AbsolutePath = diffView.AbsolutePath,
                    RepoRelativePath = diffView.RepoRelativePath,
                    DiffView = diffView
                };
                _adjacentDiffPaths = [];
                _currentDiffPathIndex = -1;
            }
            else
            {
                var adjacentDiffTask = LoadAdjacentDiffPathsAsync();
                CurrentView = await LoadWorkingTreeViewWithRetryAsync();
                await adjacentDiffTask;
            }

            _session.DiffView = CurrentView.DiffView;
            _session.ScrollProjection = CurrentView.DiffView is null ? null : GitDiffScrollProjection.FromView(CurrentView.DiffView);
            await this.InvokeAsync(() => PopulateUiAsync(scrollToFirstChange, traceOperation));
        }
        finally
        {
            _isRefreshing = false;
        }

        if (_pendingRefreshRequested)
        {
            var pendingScrollToFirstChange = _pendingScrollToFirstChange;
            _pendingRefreshRequested = false;
            _pendingScrollToFirstChange = false;
            await RefreshAsync(pendingScrollToFirstChange, traceOperation);
        }
    }

    private async Task<GitFileContentViewModel> LoadWorkingTreeViewWithRetryAsync()
    {
        const int retryCount = 3;
        var hasExistingNonEmptyView = CurrentView is { Kind: not GitFileContentViewKind.Empty };

        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            var view = await _gitService.GetFileContentView(SourcePath);
            var shouldRetry = view.Kind is GitFileContentViewKind.Empty
                && hasExistingNonEmptyView
                && File.Exists(SourcePath)
                && attempt < retryCount - 1;
            if (!shouldRetry)
            {
                return view;
            }

            await Task.Delay(120);
        }

        return await _gitService.GetFileContentView(SourcePath);
    }

    private async Task PopulateUiAsync(bool scrollToFirstChange, GitDiffTraceOperation? traceOperation = null)
    {
        var currentView = CurrentView;
        if (currentView is null) return;

        _saveButton.Visible = currentView.Kind is GitFileContentViewKind.MergeConflict;
        _diffViewportHost.Visible = currentView.Kind is GitFileContentViewKind.Diff;
        _mergeSplit.Visible = currentView.Kind is GitFileContentViewKind.MergeConflict;
        _emptyLabel.Visible = currentView.Kind is GitFileContentViewKind.Empty;
        UpdateHeaderState();

        if (currentView.Kind is GitFileContentViewKind.Diff && currentView.DiffView is { } diffView)
        {
            await PopulateDiffViewAsync(diffView, currentView.RowStatesByRowId, currentView.UnstagedActions, currentView.StagedActions, scrollToFirstChange, traceOperation);
            _emptyLabel.Visible = false;
        }
        else if (currentView.Kind is GitFileContentViewKind.MergeConflict && currentView.MergeConflictView is { } mergeView)
        {
            ClearDiffEditors();
            _emptyLabel.Visible = false;
            _localCodeEdit.Text = mergeView.LocalText;
            _currentCodeEdit.Text = mergeView.CurrentText;
            _incomingCodeEdit.Text = mergeView.IncomingText;
        }
        else
        {
            ClearDiffEditors();
            _emptyLabel.Text = "Nothing to preview.";
        }
    }

    private void ApplyViewerChrome()
    {
        _baseEditorPanel.AddThemeStyleboxOverride("panel", GitDiffPalette.CreateEditorPanelStyle(drawLeftBorder: true, drawRightBorder: false));
        _currentEditorPanel.AddThemeStyleboxOverride("panel", GitDiffPalette.CreateEditorPanelStyle(drawLeftBorder: false, drawRightBorder: true));
        _actionGutterPanel.AddThemeStyleboxOverride("panel", GitDiffPalette.CreateGutterPanelStyle());
        _summaryLabel.AddThemeColorOverride("font_color", GitDiffPalette.HeaderTextColor);
        _baseEditorLabel.AddThemeColorOverride("font_color", GitDiffPalette.HeaderTextColor);
        _currentEditorLabel.AddThemeColorOverride("font_color", GitDiffPalette.HeaderTextColor);
        _emptyLabel.AddThemeColorOverride("font_color", GitDiffPalette.SubtleTextColor);
        _connectorOverlay.InvalidateLayout();
    }

    private async Task PopulateDiffViewAsync(
        GitDiffViewModel diffView,
        IReadOnlyDictionary<string, GitDiffRowState>? rowStatesByRowId,
        GitDiffActionModel? unstagedActions,
        GitDiffActionModel? stagedActions,
        bool scrollToFirstChange,
        GitDiffTraceOperation? traceOperation)
    {
        using var activity = traceOperation?.StartChild($"{nameof(GitDiffViewer)}.{nameof(PopulateDiffViewAsync)}")
            ?? SharpIdeOtel.Source.StartActivity($"{nameof(GitDiffViewer)}.{nameof(PopulateDiffViewAsync)}");
        _baseEditorLabel.Text = diffView.BaseLabel;
        _currentEditorLabel.Text = diffView.CurrentLabel;

        var solution = _solutionAccessor.SolutionModel;
        var sharpIdeFile = ResolveSharpIdeFile(solution, diffView.AbsolutePath) ?? SharpIdeFile.CreateStandalone(diffView.AbsolutePath);

        EnsureDiffEditorsCreated(solution);
        _actionGutter.Configure(diffView, rowStatesByRowId, unstagedActions, stagedActions);
        _connectorOverlay.Configure(diffView, rowStatesByRowId);
        await this.InvokeAsync(() =>
        {
            _suppressCurrentEditorChanged = true;
            _suppressLinkedScrollSync = true;
        });

        try
        {
            var setBasePreviewTask = Task.CompletedTask;
            if (_baseEditor is not null &&
                (!IsSameFilePath(_session.BoundFile, sharpIdeFile) || !string.Equals(_session.BaseDisplayText, diffView.BaseDisplayText, StringComparison.Ordinal)))
            {
                setBasePreviewTask = SetBasePreviewTextAsync();
            }

            var setCurrentPreviewTask = Task.CompletedTask;
            if (_currentDiffEditor is not null && ShouldResetCurrentEditor(diffView, sharpIdeFile))
            {
                setCurrentPreviewTask = SetCurrentEditorTextAsync();
            }

            await Task.WhenAll(setBasePreviewTask, setCurrentPreviewTask);

            await this.InvokeAsync(() =>
            {
                ArmDiffRedrawTrace(traceOperation);

                if (_baseEditor is not null)
                {
                    ConfigureDiffEditor(_baseEditor);
                }

                if (_currentDiffEditor is not null)
                {
                    ConfigureDiffEditor(_currentDiffEditor);
                }

                using (traceOperation?.StartChild($"{nameof(GitDiffViewer)}.ApplyLineHighlights"))
                {
                    _baseEditor?.SetGitDiffLineBackgrounds(GitDiffEditorDecorations.BuildLineHighlights(diffView, rowStatesByRowId, isLeftSide: true));
                    _currentDiffEditor?.SetGitDiffLineBackgrounds(GitDiffEditorDecorations.BuildLineHighlights(diffView, rowStatesByRowId, isLeftSide: false));
                }

                using (traceOperation?.StartChild($"{nameof(GitDiffViewer)}.ApplyInlineHighlights"))
                {
                    _baseEditor?.SetGitDiffInlineHighlights(GitDiffEditorDecorations.BuildInlineHighlights(diffView, rowStatesByRowId, isLeftSide: true));
                    _currentDiffEditor?.SetGitDiffInlineHighlights(GitDiffEditorDecorations.BuildInlineHighlights(diffView, rowStatesByRowId, isLeftSide: false));
                }

                using (traceOperation?.StartChild($"{nameof(GitDiffViewer)}.ApplyScrollMarkers"))
                {
                    _baseScrollbarOverlay.SetMarkers(GitDiffScrollMarkerBuilder.BuildBaseDocumentMarkers(diffView, rowStatesByRowId));
                    _currentDiffEditor?.SetGitDiffScrollMarkers(GitDiffScrollMarkerBuilder.BuildCurrentDocumentMarkers(diffView, rowStatesByRowId));
                }

                _session.BoundFile = sharpIdeFile;
                SyncBaseExternalScrollBar();
                _actionGutter.InvalidateLayout();
                SyncGutterSpacerHeight();
                using (traceOperation?.StartChild($"{nameof(GitDiffViewer)}.ConfigureDiffChrome"))
                {
                    _actionGutter.Configure(diffView, rowStatesByRowId, unstagedActions, stagedActions);
                    _connectorOverlay.Configure(diffView, rowStatesByRowId);
                }
            });

            await this.InvokeDeferredAsync(() =>
            {
                using var deferredUiActivity = traceOperation?.StartChild($"{nameof(GitDiffViewer)}.FinalizeDiffRedraw");
                RestoreEditorViewportState(scrollToFirstChange);
                _actionGutter.InvalidateLayout();
                _connectorOverlay.InvalidateLayout();
            });
        }
        finally
        {
            await this.InvokeAsync(() =>
            {
                _suppressLinkedScrollSync = false;
                _suppressCurrentEditorChanged = false;
            });
        }

        async Task SetBasePreviewTextAsync()
        {
            using var setBasePreviewActivity = traceOperation?.StartChild($"{nameof(GitDiffViewer)}.SetBasePreviewText");
            await _baseEditor!.SetPreviewTextForFile(sharpIdeFile, diffView.BaseDisplayText, editable: false, clearGitDiffDecorations: false);
            _session.BaseDisplayText = diffView.BaseDisplayText;
        }

        bool ShouldResetCurrentEditor(GitDiffViewModel currentDiffView, SharpIdeFile currentFile)
        {
            if (_currentDiffEditor is null)
            {
                return false;
            }

            if (ShouldUseBoundCurrentEditor(currentDiffView))
            {
                return !IsSameFilePath(_session.BoundFile, currentFile);
            }

            return !IsSameFilePath(_session.BoundFile, currentFile)
                   || !string.Equals(_session.CurrentDisplayText, currentDiffView.CurrentDisplayText, StringComparison.Ordinal);
        }

        async Task SetCurrentEditorTextAsync()
        {
            using var setCurrentPreviewActivity = traceOperation?.StartChild($"{nameof(GitDiffViewer)}.SetCurrentPreviewText");
            if (ShouldUseBoundCurrentEditor(diffView))
            {
                await _currentDiffEditor!.SetSharpIdeFile(sharpIdeFile);
                _currentDiffEditor.Editable = true;
            }
            else
            {
                await _currentDiffEditor!.SetPreviewTextForFile(sharpIdeFile, diffView.CurrentDisplayText, editable: diffView.CanEditCurrent, clearGitDiffDecorations: false);
            }

            _session.CurrentDisplayText = diffView.CurrentDisplayText;
        }
    }

    private void EnsureDiffEditorsCreated(SharpIdeSolutionModel? solution)
    {
        if (_baseEditor is null)
        {
            _baseEditorContainer = SharpIdeCodeEditScene.Instantiate<SharpIdeCodeEditContainer>();
            _baseEditor = _baseEditorContainer.CodeEdit;
            _baseEditor.Solution = solution;
            _baseEditor.Editable = false;
            _baseEditorContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _baseEditorContainer.SizeFlagsVertical = SizeFlags.ExpandFill;
            _baseEditorContainer.CustomMinimumSize = new Vector2(0f, 420f);
            _baseEditor.AddThemeColorOverride("current_line_color", Colors.Transparent);
            ConfigureDiffEditor(_baseEditor);
            _baseEditor.GuiInput += OnBaseDiffEditorGuiInput;
            _baseEditorContentHost.AddChild(_baseEditorContainer);
            ApplyBaseEditorChrome();
            _baseScrollbarOverlay.Bind(
                _baseExternalScrollBar,
                _baseEditor.GetLineCount,
                _baseEditor.NavigateToGitChange,
                GitChangeScrollbarOverlay.MarkerHorizontalAlignment.Left);
        }

        if (_currentDiffEditor is null)
        {
            _currentDiffEditorContainer = SharpIdeCodeEditScene.Instantiate<SharpIdeCodeEditContainer>();
            _currentDiffEditor = _currentDiffEditorContainer.CodeEdit;
            _currentDiffEditor.Solution = solution;
            _currentDiffEditorContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _currentDiffEditorContainer.SizeFlagsVertical = SizeFlags.ExpandFill;
            _currentDiffEditorContainer.CustomMinimumSize = new Vector2(0f, 420f);
            _currentDiffEditor.AddThemeColorOverride("current_line_color", Colors.Transparent);
            ConfigureDiffEditor(_currentDiffEditor);
            _currentDiffEditor.GuiInput += OnCurrentDiffEditorGuiInput;
            _currentDiffEditor.TextChanged += OnCurrentDiffEditorTextChanged;
            _currentDiffEditor.CaretChanged += UpdateHeaderState;
            _currentEditorHost.AddChild(_currentDiffEditorContainer);
        }

        _actionGutter.BindEditors(_baseEditor, _currentDiffEditor);
        _connectorOverlay.BindEditors(_baseEditor, _currentDiffEditor);
        WireEditorScrollSync();
        ApplyBaseEditorChrome();
        SyncBaseExternalScrollBar();
        SyncGutterSpacerHeight();
        _connectorOverlay.InvalidateLayout();
    }

    private void ClearDiffEditors()
    {
        _diffViewportHost.Visible = false;
        _connectorOverlay.Configure(null);
        _connectorOverlay.ClearBindings();
        _actionGutter.ClearBindings();
        var baseEditor = _baseEditor;
        if (baseEditor?.IsAlive() == true)
        {
            baseEditor!.GuiInput -= OnBaseDiffEditorGuiInput;
        }

        var currentDiffEditor = _currentDiffEditor;
        if (currentDiffEditor?.IsAlive() == true)
        {
            currentDiffEditor!.GuiInput -= OnCurrentDiffEditorGuiInput;
            currentDiffEditor.TextChanged -= OnCurrentDiffEditorTextChanged;
            currentDiffEditor.CaretChanged -= UpdateHeaderState;
        }

        _baseScrollbarOverlay.ClearMarkers();

        _baseEditorContentHost.QueueFreeChildren();
        _currentEditorHost.QueueFreeChildren();
        _baseEditorContainer = null;
        _currentDiffEditorContainer = null;
        _baseEditor = null;
        _currentDiffEditor = null;
    }

    private void OnCurrentDiffEditorTextChanged()
    {
        if (_suppressCurrentEditorChanged || _currentDiffEditor is null) return;

        RegisterCurrentEditorTextChange();
    }

    private void RegisterCurrentEditorTextChange(bool skipRefresh = false)
    {
        if (_currentDiffEditor is null) return;

        CaptureEditorViewportState();
        _session.CurrentDisplayText = _currentDiffEditor.Text;
        _session.Revision++;
        if (skipRefresh)
        {
            return;
        }

        DebounceEditRefresh(_session.Revision);
    }

    private void WireEditorScrollSync()
    {
        if (_baseEditor is null || _currentDiffEditor is null) return;

        var baseScrollBar = _baseEditor.GetVScrollBar();
        var currentScrollBar = _currentDiffEditor.GetVScrollBar();
        var baseHorizontalScrollBar = _baseEditor.GetHScrollBar();
        var currentHorizontalScrollBar = _currentDiffEditor.GetHScrollBar();

        RewireValueChanged(baseScrollBar, OnBaseVerticalScrollChanged);
        RewireValueChanged(currentScrollBar, OnCurrentVerticalScrollChanged);
        RewireValueChanged(baseHorizontalScrollBar, OnBaseHorizontalScrollChanged);
        RewireValueChanged(currentHorizontalScrollBar, OnCurrentHorizontalScrollChanged);
    }

    private static void RewireValueChanged(global::Godot.Range scrollBar, Action<double> handler)
    {
        var callable = Callable.From(handler);
        if (scrollBar.IsConnected(global::Godot.Range.SignalName.ValueChanged, callable))
        {
            scrollBar.Disconnect(global::Godot.Range.SignalName.ValueChanged, callable);
        }

        scrollBar.Connect(global::Godot.Range.SignalName.ValueChanged, callable);
    }

    private void OnBaseVerticalScrollChanged(double value)
    {
        SyncVerticalScroll(GitDiffEditorSide.Left);
        RefreshDiffScrollChrome();
    }

    private void OnBaseHorizontalScrollChanged(double value)
    {
        SyncHorizontalScroll(GitDiffEditorSide.Left);
    }

    private void OnBaseExternalScrollChanged(double value)
    {
        if (_syncingBaseExternalScroll || _baseEditor is null) return;

        _syncingBaseExternalScroll = true;
        try
        {
            _baseEditor.SetVScroll(value);
        }
        finally
        {
            _syncingBaseExternalScroll = false;
        }
    }

    private void OnCurrentVerticalScrollChanged(double value)
    {
        SyncVerticalScroll(GitDiffEditorSide.Right);
        RefreshDiffScrollChrome();
    }

    private void OnCurrentHorizontalScrollChanged(double value)
    {
        SyncHorizontalScroll(GitDiffEditorSide.Right);
    }

    private void SyncVerticalScroll(GitDiffEditorSide sourceSide)
    {
        if (_syncingScroll || _suppressLinkedScrollSync || _baseEditor is null || _currentDiffEditor is null || _session.ScrollProjection is null) return;

        _syncingScroll = true;
        try
        {
            ApplyLinkedScrollCore(sourceSide);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void SyncHorizontalScroll(GitDiffEditorSide sourceSide)
    {
        if (_syncingHorizontalScroll || _suppressLinkedScrollSync || _baseEditor is null || _currentDiffEditor is null) return;

        _syncingHorizontalScroll = true;
        try
        {
            ApplyLinkedHorizontalScrollCore(sourceSide);
        }
        finally
        {
            _syncingHorizontalScroll = false;
        }
    }

    private void OnBaseDiffEditorGuiInput(InputEvent @event)
    {
        if (_baseEditor is null || _currentDiffEditor is null) return;
        ForwardBoundaryWheelScrollIfNeeded(@event, _baseEditor, _currentDiffEditor);
    }

    private void OnCurrentDiffEditorGuiInput(InputEvent @event)
    {
        if (_baseEditor is null || _currentDiffEditor is null) return;
        ForwardBoundaryWheelScrollIfNeeded(@event, _currentDiffEditor, _baseEditor);
    }

    private void ForwardBoundaryWheelScrollIfNeeded(
        InputEvent @event,
        SharpIdeCodeEdit sourceEditor,
        SharpIdeCodeEdit targetEditor)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown or MouseButton.WheelUp } mouseEvent)
        {
            return;
        }

        var direction = mouseEvent.ButtonIndex is MouseButton.WheelDown ? 1d : -1d;
        var sourceScrollBar = sourceEditor.GetVScrollBar();
        var targetScrollBar = targetEditor.GetVScrollBar();
        var sourceBoundary = direction > 0d ? GetScrollUpperBound(sourceScrollBar) : sourceScrollBar.MinValue;
        var sourceScroll = sourceEditor.GetVScroll();
        var isSourceAtBoundary = direction > 0d
            ? sourceScroll >= sourceBoundary - 0.01d
            : sourceScroll <= sourceBoundary + 0.01d;
        if (!isSourceAtBoundary)
        {
            return;
        }

        var targetUpperBound = GetScrollUpperBound(targetScrollBar);
        var targetStep = Math.Max(1d, targetScrollBar.Step);
        var unclampedTargetScroll = targetEditor.GetVScroll() + (direction * targetStep);
        var clampedTargetScroll = Math.Clamp(unclampedTargetScroll, targetScrollBar.MinValue, targetUpperBound);
        if (Math.Abs(clampedTargetScroll - targetEditor.GetVScroll()) < 0.01d)
        {
            return;
        }

        var targetSide = ReferenceEquals(targetEditor, _baseEditor)
            ? GitDiffEditorSide.Left
            : GitDiffEditorSide.Right;
        _suppressLinkedScrollSync = true;
        try
        {
            sourceEditor.SetVScroll(sourceBoundary);
            targetEditor.SetVScroll(clampedTargetScroll);
            ApplyLinkedScrollCore(targetSide);
        }
        finally
        {
            _suppressLinkedScrollSync = false;
        }

        sourceEditor.AcceptEvent();
        RefreshDiffScrollChrome();
    }

    private void ApplyLinkedScrollCore(GitDiffEditorSide sourceSide)
    {
        if (_baseEditor is null || _currentDiffEditor is null || _session.ScrollProjection is null)
        {
            return;
        }

        var sourceEditor = GetEditor(sourceSide);
        var targetEditor = GetEditor(GetOppositeSide(sourceSide));
        if (sourceEditor is null || targetEditor is null)
        {
            return;
        }

        var anchor = CaptureScrollAnchor(sourceEditor, sourceSide);
        var targetPosition = sourceSide is GitDiffEditorSide.Left
            ? _session.ScrollProjection.MapLeftToRight(anchor.Position1Based)
            : _session.ScrollProjection.MapRightToLeft(anchor.Position1Based);
        var sourceScroll = BuildScrollValue(sourceEditor, anchor.Position1Based);
        var targetScroll = BuildScrollValue(targetEditor, targetPosition);
        sourceEditor.SetVScroll(sourceScroll);
        targetEditor.SetVScroll(targetScroll);
        UpdateLinkedScrollState();
    }

    private void ApplyLinkedHorizontalScrollCore(GitDiffEditorSide sourceSide)
    {
        if (_baseEditor is null || _currentDiffEditor is null)
        {
            return;
        }

        var sourceEditor = GetEditor(sourceSide);
        var targetEditor = GetEditor(GetOppositeSide(sourceSide));
        if (sourceEditor is null || targetEditor is null)
        {
            return;
        }

        var sourceScrollBar = sourceEditor.GetHScrollBar();
        var targetScrollBar = targetEditor.GetHScrollBar();
        targetScrollBar.Value = ClampScrollValue(targetScrollBar, sourceScrollBar.Value);
        UpdateHorizontalScrollState();
    }

    private ScrollAnchor CaptureScrollAnchor(SharpIdeCodeEdit editor, GitDiffEditorSide side)
    {
        var scroll = ClampScrollValue(editor, editor.GetVScroll());
        return new ScrollAnchor(
            Scroll: scroll,
            Position1Based: scroll + 1d);
    }

    private void UpdateLinkedScrollState()
    {
        if (_baseEditor?.IsAlive() != true || _currentDiffEditor?.IsAlive() != true)
        {
            return;
        }

        var baseEditor = _baseEditor!;
        var currentDiffEditor = _currentDiffEditor!;
        _session.LastSyncedLeftScroll = baseEditor.GetVScroll();
        _session.LastSyncedRightScroll = currentDiffEditor.GetVScroll();
        _session.LastSyncedLeftLine = baseEditor.GetFirstVisibleLine() + 1;
        _session.LastSyncedRightLine = currentDiffEditor.GetFirstVisibleLine() + 1;
        _session.VerticalScroll = currentDiffEditor.GetVScroll();
        _session.FirstVisibleRightLine = currentDiffEditor.GetFirstVisibleLine() + 1;
    }

    private void UpdateHorizontalScrollState()
    {
        if (_currentDiffEditor?.IsAlive() != true)
        {
            return;
        }

        _session.HorizontalScroll = _currentDiffEditor!.GetHScrollBar().Value;
    }

    private void RefreshDiffScrollChrome()
    {
        SyncBaseExternalScrollBar();
        _actionGutter.InvalidateLayout();
        _connectorOverlay.InvalidateLayout();
    }

    private void SyncBaseExternalScrollBar()
    {
        if (_baseEditor?.IsAlive() != true) return;

        var baseEditor = _baseEditor!;
        var internalScrollBar = baseEditor.GetVScrollBar();
        _syncingBaseExternalScroll = true;
        try
        {
            _baseExternalScrollBar.MinValue = internalScrollBar.MinValue;
            _baseExternalScrollBar.MaxValue = internalScrollBar.MaxValue;
            _baseExternalScrollBar.Page = internalScrollBar.Page;
            _baseExternalScrollBar.Step = Math.Max(1d, internalScrollBar.Step);
            _baseExternalScrollBar.Value = internalScrollBar.Value;
            _baseExternalScrollBar.Visible = baseEditor.GetLineCount() > baseEditor.GetVisibleLineCount();
            _baseScrollbarOverlay.Visible = _baseExternalScrollBar.Visible;
            _baseScrollbarOverlay.RefreshLayout();
        }
        finally
        {
            _syncingBaseExternalScroll = false;
        }
    }

    private void ApplyBaseEditorChrome()
    {
        if (_baseEditor is null) return;

        var internalScrollBar = _baseEditor.GetVScrollBar();
        internalScrollBar.Visible = false;
        internalScrollBar.Modulate = new Color(1f, 1f, 1f, 0f);
        internalScrollBar.MouseFilter = Control.MouseFilterEnum.Ignore;
        internalScrollBar.FocusMode = Control.FocusModeEnum.None;
    }

    private bool ShouldUseBoundCurrentEditor(GitDiffViewModel diffView)
    {
        return !_isHistoricalReadOnly && diffView.CanEditCurrent;
    }

    private static void ConfigureDiffEditor(SharpIdeCodeEdit editor)
    {
        editor.GuttersDrawBreakpointsGutter = false;
        editor.GuttersDrawExecutingLines = false;
        editor.GuttersDrawLineNumbers = false;
        OverrideEditorStylebox(editor, "normal");
        OverrideEditorStylebox(editor, "read_only");
        OverrideEditorStylebox(editor, "focus");
    }

    private static void OverrideEditorStylebox(SharpIdeCodeEdit editor, string styleName)
    {
        if (editor.GetThemeStylebox(styleName) is StyleBoxFlat sourceStyle)
        {
            var style = (StyleBoxFlat)sourceStyle.Duplicate();
            style.BorderWidthLeft = 0;
            style.BorderWidthTop = 0;
            style.BorderWidthRight = 0;
            style.BorderWidthBottom = 0;
            style.ContentMarginLeft = 0;
            style.ContentMarginTop = 0;
            style.ContentMarginRight = 0;
            style.ContentMarginBottom = 0;
            style.ExpandMarginLeft = 0;
            style.ExpandMarginTop = 0;
            style.ExpandMarginRight = 0;
            style.ExpandMarginBottom = 0;
            editor.AddThemeStyleboxOverride(styleName, style);
            return;
        }

        editor.AddThemeStyleboxOverride(styleName, new StyleBoxFlat
        {
            BgColor = GitDiffPalette.PanelBackgroundColor,
            BorderWidthLeft = 0,
            BorderWidthTop = 0,
            BorderWidthRight = 0,
            BorderWidthBottom = 0,
            ContentMarginLeft = 0,
            ContentMarginTop = 0,
            ContentMarginRight = 0,
            ContentMarginBottom = 0,
            ExpandMarginLeft = 0,
            ExpandMarginTop = 0,
            ExpandMarginRight = 0,
            ExpandMarginBottom = 0
        });
    }

    private void SyncGutterSpacerHeight()
    {
        var labelHeight = Mathf.Max(_baseEditorLabel.Size.Y, _currentEditorLabel.Size.Y);
        _actionGutterSpacer.CustomMinimumSize = new Vector2(0f, labelHeight);
        _connectorOverlay.InvalidateLayout();
    }

    private void DebounceEditRefresh(long revision)
    {
        _editRefreshDebounceCts?.Cancel();
        _editRefreshDebounceCts?.Dispose();
        _editRefreshDebounceCts = new CancellationTokenSource();
        var token = _editRefreshDebounceCts.Token;
        _ = Task.GodotRun(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (revision != _session.Revision) return;
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task SaveCurrentMergeTextAsync()
    {
        var traceOperation = CreateGitActionTrace($"{nameof(GitDiffViewer)}.{nameof(SaveCurrentMergeTextAsync)}");
        await RunGitActionAsync(() => _gitService.SaveMergeConflictCurrent(SourcePath, _currentCodeEdit.Text), traceOperation);
    }

    private void NavigateToChange(bool forward)
    {
        if (CurrentView?.DiffView is not { } diffView || _currentDiffEditor is null)
        {
            return;
        }

        var changeLines = GitDiffScrollMarkerBuilder.BuildCurrentDocumentMarkers(diffView, CurrentView?.RowStatesByRowId)
            .Select(marker => marker.TargetLine)
            .Distinct()
            .Order()
            .ToArray();
        if (changeLines.Length is 0)
        {
            return;
        }

        var currentLine = _currentDiffEditor.GetCaretLine();
        var targetLine = forward
            ? changeLines.Cast<int?>().FirstOrDefault(line => line > currentLine)
            : changeLines.Cast<int?>().LastOrDefault(line => line < currentLine);

        if (!targetLine.HasValue)
        {
            return;
        }

        NavigateToPrimaryLine(targetLine.Value);
        UpdateHeaderState();
    }

    private void NavigateToPrimaryLine(int lineIndex)
    {
        if (_currentDiffEditor is null || _baseEditor is null)
        {
            return;
        }

        var safeLine = Mathf.Clamp(lineIndex, 0, Math.Max(_currentDiffEditor.GetLineCount() - 1, 0));
        _session.CaretLine = safeLine;
        _session.CaretColumn = 0;
        CenterPrimaryEditorOnLine(safeLine);
    }

    private async Task OpenSourceFileAsync()
    {
        if (_session.BoundFile is null)
        {
            return;
        }

        SharpIdeFileLinePosition? position = _currentDiffEditor is null
            ? null
            : new SharpIdeFileLinePosition(_currentDiffEditor.GetCaretLine(), _currentDiffEditor.GetCaretColumn());
        await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(_session.BoundFile, position);
    }

    private async Task NavigateToAdjacentDiffAsync(bool forward)
    {
        if (_adjacentDiffPaths.Count is 0 || _currentDiffPathIndex < 0)
        {
            return;
        }

        var nextIndex = forward ? _currentDiffPathIndex + 1 : _currentDiffPathIndex - 1;
        if (nextIndex < 0 || nextIndex >= _adjacentDiffPaths.Count)
        {
            return;
        }

        await LoadFromPath(_adjacentDiffPaths[nextIndex]);
    }

    private async Task StageLinesAsync(IReadOnlyList<string> lineActionIds)
    {
        var traceOperation = CreateGitActionTrace($"{nameof(GitDiffViewer)}.{nameof(StageLinesAsync)}");
        traceOperation.SetTag("git.diff.line_action.count", lineActionIds.Count);
        await RunGitActionAsync(() => _gitService.StageLines(SourcePath, lineActionIds), traceOperation);
    }

    private async Task StageChunkAsync(string chunkId)
    {
        var traceOperation = CreateGitActionTrace($"{nameof(GitDiffViewer)}.{nameof(StageChunkAsync)}");
        traceOperation.SetTag("git.diff.chunk_id", chunkId);
        await RunGitActionAsync(() => _gitService.StageChunk(SourcePath, chunkId), traceOperation);
    }

    private async Task UnstageLinesAsync(IReadOnlyList<string> lineActionIds)
    {
        var traceOperation = CreateGitActionTrace($"{nameof(GitDiffViewer)}.{nameof(UnstageLinesAsync)}");
        traceOperation.SetTag("git.diff.line_action.count", lineActionIds.Count);
        await RunGitActionAsync(() => _gitService.UnstageLines(SourcePath, lineActionIds), traceOperation);
    }

    private async Task UnstageChunkAsync(string chunkId)
    {
        var traceOperation = CreateGitActionTrace($"{nameof(GitDiffViewer)}.{nameof(UnstageChunkAsync)}");
        traceOperation.SetTag("git.diff.chunk_id", chunkId);
        await RunGitActionAsync(() => _gitService.UnstageChunk(SourcePath, chunkId), traceOperation);
    }

    private async Task RevertChunkAsync(string chunkId)
    {
        var traceOperation = CreateGitActionTrace($"{nameof(GitDiffViewer)}.{nameof(RevertChunkAsync)}");
        traceOperation.SetTag("git.diff.chunk_id", chunkId);
        await RunGitRevertActionAsync(chunkId, traceOperation);
    }

    private void OnDividerDragged(float delta)
    {
        if (_diffEditorRow.Size.X <= 0f) return;

        var gutterWidth = _actionGutter.Size.X;
        var availableWidth = Math.Max(1f, _diffEditorRow.Size.X - gutterWidth);
        var currentLeftWidth = _baseEditorPanel.Size.X;
        var minimumPaneWidth = MathF.Min(160f, MathF.Max(48f, availableWidth * 0.2f));
        var maxLeftWidth = MathF.Max(minimumPaneWidth, availableWidth - minimumPaneWidth);
        var newLeftWidth = Mathf.Clamp(currentLeftWidth + delta, minimumPaneWidth, maxLeftWidth);
        var newRightWidth = availableWidth - newLeftWidth;
        _baseEditorPanel.SizeFlagsStretchRatio = newLeftWidth / availableWidth;
        _currentEditorPanel.SizeFlagsStretchRatio = newRightWidth / availableWidth;
        _baseEditorPanel.CustomMinimumSize = new Vector2(0f, 0f);
        _currentEditorPanel.CustomMinimumSize = new Vector2(0f, 0f);
        _diffEditorRow.QueueSort();
        _connectorOverlay.InvalidateLayout();
    }

    private async Task RunGitActionAsync(Func<Task> action, GitDiffTraceOperation traceOperation)
    {
        if (_isRunningGitAction)
        {
            traceOperation.Complete("action_skipped");
            return;
        }

        CaptureEditorViewportState();
        _isRunningGitAction = true;
        _actionGutter.SetBusy(true);
        try
        {
            await SaveBoundFileIfDirtyAsync();
            _suppressRepositoryRefreshUntil = DateTimeOffset.UtcNow.AddMilliseconds(750);
            using (traceOperation.StartChild($"{nameof(GitDiffViewer)}.ApplyGitAction"))
            {
                await action();
            }
        }
        catch (Exception ex)
        {
            traceOperation.SetTag("git.diff.error", ex.Message);
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            try
            {
                using (traceOperation.StartChild($"{nameof(GitDiffViewer)}.RefreshAfterGitAction"))
                {
                    await RefreshAsync(traceOperation: traceOperation);
                }

                traceOperation.CompleteIfNoPendingRedraws("refresh_completed");
            }
            finally
            {
                _isRunningGitAction = false;
                _actionGutter.SetBusy(false);
            }
        }
    }

    private async Task RunGitRevertActionAsync(string chunkId, GitDiffTraceOperation traceOperation)
    {
        if (_isRunningGitAction)
        {
            traceOperation.Complete("action_skipped");
            return;
        }

        CaptureEditorViewportState();
        _isRunningGitAction = true;
        _actionGutter.SetBusy(true);
        try
        {
            await SaveBoundFileIfDirtyAsync();
            _suppressRepositoryRefreshUntil = DateTimeOffset.UtcNow.AddMilliseconds(750);
            using (traceOperation.StartChild($"{nameof(GitDiffViewer)}.ApplyGitAction"))
            {
                await _gitService.RevertChunk(SourcePath, chunkId);
            }

            var updatedText = File.Exists(SourcePath)
                ? await File.ReadAllTextAsync(SourcePath)
                : string.Empty;
            using (traceOperation.StartChild($"{nameof(GitDiffViewer)}.{nameof(ApplyUpdatedTextInEditorAsync)}"))
            {
                await ApplyUpdatedTextInEditorAsync(updatedText);
            }
        }
        catch (Exception ex)
        {
            traceOperation.SetTag("git.diff.error", ex.Message);
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            try
            {
                using (traceOperation.StartChild($"{nameof(GitDiffViewer)}.RefreshAfterGitAction"))
                {
                    await RefreshAsync(traceOperation: traceOperation);
                }

                traceOperation.CompleteIfNoPendingRedraws("refresh_completed");
            }
            finally
            {
                _isRunningGitAction = false;
                _actionGutter.SetBusy(false);
            }
        }
    }

    private async Task SaveBoundFileIfDirtyAsync()
    {
        if (_session.BoundFile is not { } boundFile || boundFile.IsDirty.Value is false || _currentDiffEditor is null)
        {
            return;
        }

        await _fileChangedService.SharpIdeFileChanged(boundFile, _currentDiffEditor.Text, FileChangeType.IdeSaveToDisk);
    }

    private async Task ShowErrorAsync(string message)
    {
        AcceptDialog? dialog = null;
        await this.InvokeAsync(() =>
        {
            dialog = new AcceptDialog
            {
                Title = "Git Action Failed",
                DialogText = message
            };
            AddChild(dialog);
        });

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog!.Confirmed += () => tcs.TrySetResult();
        dialog.CloseRequested += () => tcs.TrySetResult();
        await this.InvokeAsync(() => dialog.PopupCentered());
        await tcs.Task;
        await this.InvokeDeferredAsync(() => dialog.QueueFree());
    }

    private static SharpIdeFile? ResolveSharpIdeFile(SharpIdeSolutionModel? solution, string absolutePath)
    {
        if (solution is null) return null;

        var normalizedPath = Path.GetFullPath(absolutePath);
        if (solution.AllFiles.TryGetValue(normalizedPath, out var directMatch))
        {
            return directMatch;
        }

        return solution.AllFiles.Values.FirstOrDefault(file =>
            string.Equals(Path.GetFullPath(file.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameFilePath(SharpIdeFile? left, SharpIdeFile? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left.Path),
            Path.GetFullPath(right.Path),
            StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureEditorViewportState()
    {
        if (_currentDiffEditor?.IsAlive() != true) return;
        var currentDiffEditor = _currentDiffEditor!;
        _session.HorizontalScroll = currentDiffEditor.GetHScrollBar().Value;
        _session.VerticalScroll = currentDiffEditor.GetVScroll();
        _session.CaretLine = currentDiffEditor.GetCaretLine();
        _session.CaretColumn = currentDiffEditor.GetCaretColumn();
        _session.FirstVisibleRightLine = currentDiffEditor.GetFirstVisibleLine() + 1;
        UpdateLinkedScrollState();
    }

    private void RestoreEditorViewportState(bool scrollToFirstChange)
    {
        if (_currentDiffEditor is null || _baseEditor is null) return;
        if (scrollToFirstChange && CurrentView?.DiffView is { } diffView)
        {
            ScrollToFirstChangedRow(diffView);
            return;
        }

        var safeLine = Mathf.Clamp(_session.CaretLine, 0, Math.Max(_currentDiffEditor.GetLineCount() - 1, 0));
        _currentDiffEditor.SetCaretLine(safeLine);
        _currentDiffEditor.SetCaretColumn(Mathf.Clamp(_session.CaretColumn, 0, _currentDiffEditor.GetLine(safeLine).Length));
        _currentDiffEditor.GetHScrollBar().Value = ClampScrollValue(_currentDiffEditor.GetHScrollBar(), _session.HorizontalScroll);
        _currentDiffEditor.SetVScroll(ClampScrollValue(_currentDiffEditor, _session.VerticalScroll));
        var rightLine = _currentDiffEditor.GetFirstVisibleLine() + 1;
        if (rightLine <= 0)
        {
            rightLine = Math.Max(1, _session.FirstVisibleRightLine);
            _currentDiffEditor.SetVScroll(BuildScrollValue(_currentDiffEditor, rightLine));
        }

        ApplyLinkedHorizontalScrollCore(GitDiffEditorSide.Right);
        ApplyLinkedScrollCore(GitDiffEditorSide.Right);
        RefreshDiffScrollChrome();
    }

    private void ScrollToFirstChangedRow(GitDiffViewModel diffView)
    {
        if (_currentDiffEditor is null || _baseEditor is null) return;

        var firstChangedRow = diffView.Rows.FirstOrDefault(static row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None);
        if (firstChangedRow is null)
        {
            _currentDiffEditor.SetVScroll(0);
            ApplyLinkedScrollCore(GitDiffEditorSide.Right);
            RefreshDiffScrollChrome();
            return;
        }

        var targetLine = firstChangedRow.RightFileLineNumber
            ?? firstChangedRow.LeftFileLineNumber
            ?? 1;
        var lineIndex = Math.Max(0, targetLine - 1);
        _session.CaretLine = lineIndex;
        _session.CaretColumn = 0;
        _session.VerticalScroll = lineIndex;
        _session.FirstVisibleRightLine = targetLine;
        CenterPrimaryEditorOnLine(lineIndex);
    }

    private void CenterPrimaryEditorOnLine(int lineIndex)
    {
        if (_currentDiffEditor is null)
        {
            return;
        }

        _currentDiffEditor.NavigateToGitChange(lineIndex);
        ApplyLinkedScrollCore(GitDiffEditorSide.Right);
        RefreshDiffScrollChrome();
    }

    private static double BuildScrollValue(SharpIdeCodeEdit editor, double position1Based)
    {
        var clampedPosition = Math.Clamp(position1Based, 1d, Math.Max(editor.GetLineCount(), 1));
        return ClampScrollValue(editor, clampedPosition - 1d);
    }

    private static double ClampScrollValue(SharpIdeCodeEdit editor, double value)
    {
        var scrollBar = editor.GetVScrollBar();
        return Math.Clamp(value, scrollBar.MinValue, GetScrollUpperBound(scrollBar));
    }

    private static double ClampScrollValue(global::Godot.Range scrollBar, double value)
    {
        return Math.Clamp(value, scrollBar.MinValue, GetScrollUpperBound(scrollBar));
    }

    private static double GetScrollUpperBound(global::Godot.Range scrollBar)
    {
        return Math.Max(scrollBar.MinValue, scrollBar.MaxValue - scrollBar.Page);
    }

    private SharpIdeCodeEdit? GetEditor(GitDiffEditorSide side)
    {
        return side is GitDiffEditorSide.Left ? _baseEditor : _currentDiffEditor;
    }

    private static GitDiffEditorSide GetOppositeSide(GitDiffEditorSide side)
    {
        return side is GitDiffEditorSide.Left ? GitDiffEditorSide.Right : GitDiffEditorSide.Left;
    }

    private async Task ApplyUpdatedTextInEditorAsync(string updatedText)
    {
        if (_currentDiffEditor is null) return;

        var currentText = _currentDiffEditor.Text;
        var normalizedCurrentText = currentText.Replace("\r\n", "\n");
        var normalizedUpdatedText = updatedText.Replace("\r\n", "\n");
        if (string.Equals(normalizedCurrentText, normalizedUpdatedText, StringComparison.Ordinal))
        {
            return;
        }

        var commonPrefixLength = 0;
        var maxPrefixLength = Math.Min(normalizedCurrentText.Length, normalizedUpdatedText.Length);
        while (commonPrefixLength < maxPrefixLength &&
               normalizedCurrentText[commonPrefixLength] == normalizedUpdatedText[commonPrefixLength])
        {
            commonPrefixLength++;
        }

        var commonSuffixLength = 0;
        var maxSuffixLength = Math.Min(normalizedCurrentText.Length - commonPrefixLength, normalizedUpdatedText.Length - commonPrefixLength);
        while (commonSuffixLength < maxSuffixLength &&
               normalizedCurrentText[normalizedCurrentText.Length - 1 - commonSuffixLength] ==
               normalizedUpdatedText[normalizedUpdatedText.Length - 1 - commonSuffixLength])
        {
            commonSuffixLength++;
        }

        var removedTextEnd = normalizedCurrentText.Length - commonSuffixLength;
        var insertedTextEnd = normalizedUpdatedText.Length - commonSuffixLength;
        var replacementText = normalizedUpdatedText[commonPrefixLength..insertedTextEnd];
        var (startLine, startColumn) = GetTextPosition(normalizedCurrentText, commonPrefixLength);
        var (endLine, endColumn) = GetTextPosition(normalizedCurrentText, removedTextEnd);

        var shouldRegisterTextChange = false;
        await this.InvokeAsync(() =>
        {
            _suppressCurrentEditorChanged = true;
            _suppressLinkedScrollSync = true;
            try
            {
                var currentVerticalScroll = _currentDiffEditor.GetVScroll();
                var currentHorizontalScroll = _currentDiffEditor.GetHScrollBar().Value;
                var currentCaretLine = _currentDiffEditor.GetCaretLine();
                var currentCaretColumn = _currentDiffEditor.GetCaretColumn();
                var currentFirstVisibleLine = _currentDiffEditor.GetFirstVisibleLine() + 1;

                _currentDiffEditor.BeginComplexOperation();
                if (removedTextEnd > commonPrefixLength)
                {
                    _currentDiffEditor.RemoveText(startLine, startColumn, endLine, endColumn);
                }

                _currentDiffEditor.SetCaretLine(startLine);
                _currentDiffEditor.SetCaretColumn(startColumn);
                if (!string.IsNullOrEmpty(replacementText))
                {
                    _currentDiffEditor.InsertTextAtCaret(replacementText);
                }

                _currentDiffEditor.EndComplexOperation();

                var safeCaretLine = Mathf.Clamp(currentCaretLine, 0, Math.Max(_currentDiffEditor.GetLineCount() - 1, 0));
                _currentDiffEditor.SetCaretLine(safeCaretLine);
                _currentDiffEditor.SetCaretColumn(Mathf.Clamp(currentCaretColumn, 0, _currentDiffEditor.GetLine(safeCaretLine).Length));
                _currentDiffEditor.GetHScrollBar().Value = ClampScrollValue(_currentDiffEditor.GetHScrollBar(), currentHorizontalScroll);
                _currentDiffEditor.SetVScroll(ClampScrollValue(_currentDiffEditor, currentVerticalScroll));

                ApplyLinkedScrollCore(GitDiffEditorSide.Right);
                ApplyLinkedHorizontalScrollCore(GitDiffEditorSide.Right);
                RefreshDiffScrollChrome();
                shouldRegisterTextChange = true;
            }
            finally
            {
                _suppressLinkedScrollSync = false;
                _suppressCurrentEditorChanged = false;
            }
        });

        if (shouldRegisterTextChange)
        {
            RegisterCurrentEditorTextChange(skipRefresh: true);
        }
    }

    private static (int line, int column) GetTextPosition(string text, int index)
    {
        var line = 0;
        var column = 0;
        var safeIndex = Math.Clamp(index, 0, text.Length);
        for (var i = 0; i < safeIndex; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 0;
                continue;
            }

            column++;
        }

        return (line, column);
    }

    private GitDiffTraceOperation CreateGitActionTrace(string activityName)
    {
        var traceOperation = new GitDiffTraceOperation(activityName, SourcePath);
        traceOperation.SetTag("git.diff.preview_key", PreviewKey);
        return traceOperation;
    }

    private void ArmDiffRedrawTrace(GitDiffTraceOperation? traceOperation)
    {
        if (traceOperation is null)
        {
            return;
        }

        var redrawTargets = GitDiffTraceRedrawTarget.None;
        if (_baseEditor is not null)
        {
            redrawTargets |= GitDiffTraceRedrawTarget.BaseEditor;
            _baseEditor.SetPendingGitDiffTraceOperation(traceOperation, GitDiffTraceRedrawTarget.BaseEditor);
        }

        if (_currentDiffEditor is not null)
        {
            redrawTargets |= GitDiffTraceRedrawTarget.CurrentEditor;
            _currentDiffEditor.SetPendingGitDiffTraceOperation(traceOperation, GitDiffTraceRedrawTarget.CurrentEditor);
        }

        redrawTargets |= GitDiffTraceRedrawTarget.ActionGutter;
        _actionGutter.SetPendingTraceOperation(traceOperation);
        traceOperation.ArmForRedraw(redrawTargets);
    }

    private sealed class GitDiffSessionState
    {
        public GitDiffViewModel? DiffView { get; set; }
        public GitDiffScrollProjection? ScrollProjection { get; set; }
        public long Revision { get; set; }
        public double HorizontalScroll { get; set; }
        public double VerticalScroll { get; set; }
        public double LastSyncedLeftScroll { get; set; }
        public double LastSyncedRightScroll { get; set; }
        public int LastSyncedLeftLine { get; set; }
        public int LastSyncedRightLine { get; set; }
        public int CaretLine { get; set; }
        public int CaretColumn { get; set; }
        public int FirstVisibleRightLine { get; set; } = 1;
        public string BaseDisplayText { get; set; } = string.Empty;
        public string CurrentDisplayText { get; set; } = string.Empty;
        public SharpIdeFile? BoundFile { get; set; }
    }

    private readonly record struct ScrollAnchor(
        double Scroll,
        double Position1Based);

    private sealed class GitDiffScrollProjection
    {
        private readonly double[] _leftPositions;
        private readonly double[] _rightPositions;

        private GitDiffScrollProjection(IReadOnlyList<GitDiffDisplayRow> rows)
        {
            _leftPositions = BuildSidePositions(rows, useLeftSide: true);
            _rightPositions = BuildSidePositions(rows, useLeftSide: false);
        }

        public static GitDiffScrollProjection FromView(GitDiffViewModel view) => new(view.Rows);

        public double MapLeftToRight(double leftPosition)
        {
            return MapPosition(leftPosition, _leftPositions, _rightPositions);
        }

        public double MapRightToLeft(double rightPosition)
        {
            return MapPosition(rightPosition, _rightPositions, _leftPositions);
        }

        private static double[] BuildSidePositions(IReadOnlyList<GitDiffDisplayRow> rows, bool useLeftSide)
        {
            var positions = new double[rows.Count];
            for (var index = 0; index < rows.Count; index++)
            {
                var lineNumber = useLeftSide ? rows[index].LeftFileLineNumber : rows[index].RightFileLineNumber;
                positions[index] = lineNumber ?? double.NaN;
            }

            FillMissingPositions(positions);
            return positions;
        }

        private static void FillMissingPositions(double[] positions)
        {
            var length = positions.Length;
            var firstKnownIndex = Array.FindIndex(positions, static value => !double.IsNaN(value));
            if (firstKnownIndex < 0)
            {
                Array.Fill(positions, 1d);
                return;
            }

            for (var index = 0; index < firstKnownIndex; index++)
            {
                positions[index] = positions[firstKnownIndex];
            }

            var lastKnownIndex = firstKnownIndex;
            for (var index = firstKnownIndex + 1; index < length; index++)
            {
                if (double.IsNaN(positions[index]))
                {
                    continue;
                }

                var gapCount = index - lastKnownIndex - 1;
                if (gapCount > 0)
                {
                    for (var gapIndex = 1; gapIndex <= gapCount; gapIndex++)
                    {
                        positions[lastKnownIndex + gapIndex] = positions[lastKnownIndex];
                    }
                }

                lastKnownIndex = index;
            }

            for (var index = lastKnownIndex + 1; index < length; index++)
            {
                positions[index] = positions[lastKnownIndex];
            }
        }

        private static double MapPosition(double sourcePosition, IReadOnlyList<double> sourcePositions, IReadOnlyList<double> targetPositions)
        {
            if (sourcePositions.Count is 0 || targetPositions.Count is 0)
            {
                return Math.Max(1d, sourcePosition);
            }

            if (sourcePosition <= sourcePositions[0])
            {
                return targetPositions[0];
            }

            var lastIndex = sourcePositions.Count - 1;
            if (sourcePosition >= sourcePositions[lastIndex])
            {
                return targetPositions[lastIndex];
            }

            for (var index = 1; index < sourcePositions.Count; index++)
            {
                if (sourcePosition > sourcePositions[index])
                {
                    continue;
                }

                if (Math.Abs(sourcePosition - sourcePositions[index]) < 0.0001d)
                {
                    var firstEqualIndex = index;
                    while (firstEqualIndex > 0 && Math.Abs(sourcePositions[firstEqualIndex - 1] - sourcePositions[index]) < 0.0001d)
                    {
                        firstEqualIndex--;
                    }

                    return targetPositions[firstEqualIndex];
                }

                var startSource = sourcePositions[index - 1];
                var endSource = sourcePositions[index];
                var startTarget = targetPositions[index - 1];
                var endTarget = targetPositions[index];
                var denominator = endSource - startSource;
                if (Math.Abs(denominator) < 0.0001d)
                {
                    continue;
                }

                var progress = (sourcePosition - startSource) / denominator;
                return startTarget + ((endTarget - startTarget) * progress);
            }

            return targetPositions[lastIndex];
        }
    }

    private void UpdateHeaderState()
    {
        var diffView = CurrentView?.DiffView;
        var changeCount = diffView?.Rows.Count(row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None)
            ?? 0;
        var includedCount = CurrentView?.StagedActions?.LineActions.Count ?? 0;
        _summaryLabel.Text = diffView is null ? string.Empty : $"{changeCount} differences, {includedCount} included";

        var changeTargets = diffView is null
            ? []
            : GitDiffScrollMarkerBuilder.BuildCurrentDocumentMarkers(diffView, CurrentView?.RowStatesByRowId)
                .Select(marker => marker.TargetLine)
                .Distinct()
                .Order()
                .ToArray();
        var caretLine = _currentDiffEditor?.GetCaretLine() ?? 0;
        _previousChangeButton.Disabled = changeTargets.Length is 0 || changeTargets.All(line => line >= caretLine);
        _nextChangeButton.Disabled = changeTargets.Length is 0 || changeTargets.All(line => line <= caretLine);
        _editSourceButton.Disabled = _session.BoundFile is null || _isHistoricalReadOnly;
        _previousDiffButton.Disabled = _currentDiffPathIndex <= 0;
        _nextDiffButton.Disabled = _currentDiffPathIndex < 0 || _currentDiffPathIndex >= _adjacentDiffPaths.Count - 1;
    }

    private async Task LoadAdjacentDiffPathsAsync()
    {
        var solution = _solutionAccessor.SolutionModel;
        if (solution is null || string.IsNullOrWhiteSpace(SourcePath))
        {
            _adjacentDiffPaths = [];
            _currentDiffPathIndex = -1;
            return;
        }

        var snapshot = await _gitService.GetSnapshot(solution.FilePath, commitCount: 1);
        _adjacentDiffPaths = snapshot.WorkingTreeEntries
            .Select(entry => Path.GetFullPath(entry.AbsolutePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedSourcePath = Path.GetFullPath(SourcePath);
        _currentDiffPathIndex = _adjacentDiffPaths
            .Select((path, index) => (path, index))
            .FirstOrDefault(item => string.Equals(item.path, normalizedSourcePath, StringComparison.OrdinalIgnoreCase))
            .index;
        if (_adjacentDiffPaths.Count is 0 ||
            !_adjacentDiffPaths.Any(path => string.Equals(path, normalizedSourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            _currentDiffPathIndex = -1;
        }
    }
}
