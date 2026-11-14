using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using Godot;
using Microsoft.CodeAnalysis;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.Common;

namespace SharpIDE.Godot.Features.Problems;

public partial class ProblemsPanel : Control
{
    [Export]
    public Texture2D WarningIcon { get; set; } = null!;
    [Export]
    public Texture2D ErrorIcon { get; set; } = null!;
    [Export]
    public Texture2D CsprojIcon { get; set; } = null!;

    private SharpIdeSolutionModel? _solution;

    [Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;

	private Tree _tree = null!;
    private TreeItem _rootItem = null!;

    public override void _Ready()
    {
        _diagnosticCustomDrawCallable = new Callable(this, MethodName.DiagnosticCustomDraw);
        _tree = GetNode<Tree>("%Tree");
        _tree.ItemActivated += TreeOnItemActivated;
        _rootItem = _tree.CreateItem();
        _rootItem.SetText(0, "Problems");
        _stagingTree = new Tree();
        _stagingRoot = _stagingTree.CreateItem();
        _ = Task.GodotRun(AsyncReady);
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete) return;
        // Not in the scene tree, so it isn't freed with us
        lock (_stagingLock) _stagingTree?.Free();
    }

    private async Task AsyncReady()
    {
        await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
        _solution = _sharpIdeSolutionAccessor.SolutionModel;
        BindToTree(_solution.AllProjects);
    }

    // TreeItems can only be created via Tree.CreateItem, and _tree is in the scene tree, so it can only be touched on the UI thread.
    // _stagingTree is never in the scene tree, so Godot allows it to be used from any thread (one at a time - hence the lock).
    // Items are created and configured in it off the UI thread, then detached (RemoveChild), leaving an orphaned TreeItem that belongs to no Tree.
    // The UI thread then only has to AddChild it into _tree.
    private Tree _stagingTree = null!;
    private TreeItem _stagingRoot = null!;
    private readonly Lock _stagingLock = new();

    // To avoid a long frame when an analysis pass adds/removes thousands of diagnostics at once, the UI thread's part of each change is queued (from any thread),
    // and drained on the UI thread within a per-frame time budget, so a large update is spread over several frames.
    // The queue is FIFO, and Removes read their TreeItemContainer when they run, so a Remove queued after an Add finds the item the Add created.
    private readonly ConcurrentQueue<Action> _pendingTreeOps = new();
    private readonly HashSet<TreeItem> _projectItemsToUpdateVisibility = [];
    private const double TreeOpsBudgetMs = 4;

    public override void _Process(double delta)
    {
        if (_pendingTreeOps.IsEmpty) return;
        var startTimestamp = Stopwatch.GetTimestamp();
        while (_pendingTreeOps.TryDequeue(out var op))
        {
            op();
            if (Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds >= TreeOpsBudgetMs) break;
        }
        // Once per frame per touched project, rather than per diagnostic
        foreach (var projectItem in _projectItemsToUpdateVisibility)
        {
            if (IsInstanceValid(projectItem)) projectItem.Visible = projectItem.GetFirstChild() is not null;
        }
        _projectItemsToUpdateVisibility.Clear();
    }

    /// Creates and configures an item (and any children) in the staging tree, then detaches it so it can be added to _tree on the UI thread.
    private TreeItem StageTreeItem<TState>(TState state, Action<TreeItem, TState> configure)
    {
        lock (_stagingLock)
        {
            var item = _stagingTree.CreateItem(_stagingRoot);
            configure(item, state);
            _stagingRoot.RemoveChild(item);
            return item;
        }
    }

    private void BindToTree(ObservableHashSet<SharpIdeProjectModel> list)
    {
        var view = list.CreateView(y => new TreeItemContainer());
        foreach (var s in view.Unfiltered.ToList()) StageProjectTreeItem(s.Value, s.View);
        var disposableBuilder = new DisposableBuilder();
        view.ObserveChanged().Subscribe(e =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add: StageProjectTreeItem(e.NewItem.Value, e.NewItem.View); break;
                case NotifyCollectionChangedAction.Remove: var container = e.OldItem.View; _pendingTreeOps.Enqueue(() => FreeTreeItem(container.Value)); break;
            }
        }).AddTo(ref disposableBuilder);
        _rootItem.SharpIdeDisposable = disposableBuilder.Build();
    }

    private void StageProjectTreeItem(SharpIdeProjectModel sharpIdeProjectModel, TreeItemContainer container)
    {
        var projectDiagnosticsView = sharpIdeProjectModel.Diagnostics.CreateView(y => new TreeItemContainer());
        // The project's existing diagnostics are staged as its children, so they arrive in _tree together with it in one AddChild
        var projectItem = StageTreeItem((Panel: this, Project: sharpIdeProjectModel, Diagnostics: projectDiagnosticsView.Unfiltered.ToList()), static (item, state) =>
        {
            item.SetText(0, state.Project.Name.Value);
            item.SetIcon(0, state.Panel.CsprojIcon);
            item.Visible = false; // Made visible once it is in _tree, if it has diagnostics
            foreach (var s in state.Diagnostics)
            {
                var diagItem = item.CreateChild();
                state.Panel.ConfigureDiagnosticTreeItem(diagItem, s.Value);
                s.View.Value = diagItem;
            }
        });
        container.Value = projectItem;
        // Queued before subscribing, so this always runs before any of the project's later diagnostic changes
        _pendingTreeOps.Enqueue(() =>
        {
            _rootItem.AddChild(projectItem);
            _projectItemsToUpdateVisibility.Add(projectItem);
        });

        var disposableBuilder = new DisposableBuilder();
        projectDiagnosticsView.ObserveChanged().Subscribe(innerEvent =>
        {
            switch (innerEvent.Action)
            {
                case NotifyCollectionChangedAction.Add: StageDiagnosticTreeItem(projectItem, innerEvent.NewItem.Value, innerEvent.NewItem.View); break;
                case NotifyCollectionChangedAction.Remove:
                    var diagContainer = innerEvent.OldItem.View;
                    _pendingTreeOps.Enqueue(() =>
                    {
                        FreeTreeItem(diagContainer.Value);
                        _projectItemsToUpdateVisibility.Add(projectItem);
                    });
                    break;
            }
        }).AddTo(ref disposableBuilder);
        projectItem.SharpIdeDisposable = disposableBuilder.Build();
    }

    private void StageDiagnosticTreeItem(TreeItem projectItem, SharpIdeDiagnostic diagnostic, TreeItemContainer container)
    {
        var diagItem = StageTreeItem((Panel: this, Diagnostic: diagnostic), static (item, state) => state.Panel.ConfigureDiagnosticTreeItem(item, state.Diagnostic));
        container.Value = diagItem;
        _pendingTreeOps.Enqueue(() =>
        {
            if (IsInstanceValid(projectItem) is false)
            {
                // The project was removed (and its item freed) before this ran. diagItem is orphaned, so wasn't freed with it.
                diagItem.Free();
                return;
            }
            projectItem.AddChild(diagItem);
            _projectItemsToUpdateVisibility.Add(projectItem);
        });
    }

    private Callable? _diagnosticCustomDrawCallable;
    private TextLine _diagnosticTextLine = new TextLine(); // Reusing this is based on the assumption that it is called by godot in a single-threaded fashion
    private void DiagnosticCustomDraw(TreeItem treeItem, Rect2 rect)
    {
        var hovered = _tree.GetItemAtPosition(_tree.GetLocalMousePosition()) == treeItem;
        var isSelected = treeItem.IsSelected(0);

        var diagnostic = treeItem.SharpIdeDiagnostic;
        if (diagnostic is null) return;

        var message = diagnostic.Message;
        var severity = diagnostic.Severity;
        var linePosition = diagnostic.Span.Start;

        // Get icon based on severity
        var icon = severity switch
        {
            DiagnosticSeverity.Error => ErrorIcon,
            DiagnosticSeverity.Warning => WarningIcon,
            _ => null
        };

        // Define padding and spacing
        const float padding = 4.0f;
        const float iconSize = 22.0f;
        const float spacing = 6.0f;

        var currentX = rect.Position.X + padding;
        var currentY = rect.Position.Y;

        // Draw icon
        if (icon is not null)
        {
            var iconRect = new Rect2(currentX, currentY + (rect.Size.Y - iconSize) / 2, iconSize, iconSize);
            _tree.DrawTextureRect(icon, iconRect, false);
            currentX += iconSize + spacing;
        }

        // Get font and prepare text
        var font = _tree.GetThemeFont(ThemeStringNames.Font);
        var fontSize = _tree.GetThemeFontSize(ThemeStringNames.FontSize);
        var textColor = (isSelected, hovered) switch
        {
            (true, true) => _tree.GetThemeColor(ThemeStringNames.FontHoveredSelectedColor),
            (true, false) => _tree.GetThemeColor(ThemeStringNames.FontSelectedColor),
            (false, true) => _tree.GetThemeColor(ThemeStringNames.FontHoveredColor),
            (false, false) => _tree.GetThemeColor(ThemeStringNames.FontColor)
        };
        var textYPos = currentY + (rect.Size.Y + fontSize) / 2 - 2;

        // Calculate right-hand text widths first
        var fileName = Path.GetFileName(diagnostic.FilePath);
        var fileNameWidth = font.GetStringSize(fileName, HorizontalAlignment.Left, -1, fontSize).X;
        var locationText = $"({linePosition.Line + 1}:{linePosition.Character + 1})";
        var locationWidth = font.GetStringSize(locationText, HorizontalAlignment.Left, -1, fontSize).X;
        var rightSideWidth = locationWidth + spacing + fileNameWidth + padding;

        var textLine = _diagnosticTextLine;
        textLine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        textLine.SetHorizontalAlignment(HorizontalAlignment.Left);
        textLine.AddString(message, font, fontSize);

        // Draw message with width constraint to avoid overlap
        var maxMessageWidth = rect.Size.X - currentX - rightSideWidth - spacing;
        if (maxMessageWidth > 0)
        {
            textLine.Width = maxMessageWidth;
            textLine.Draw(_tree.GetCanvasItem(), new Vector2(currentX, textYPos - textLine.GetLineAscent()), textColor);
            textLine.Clear();
            //_tree.DrawString(font, new Vector2(currentX, textYPos), message, HorizontalAlignment.Left, maxMessageWidth, fontSize, textColor);
        }

        // Draw location info (line:column) on the right side
        var locationX = rect.Position.X + rect.Size.X - rightSideWidth;
        var locationColor = textColor with { A = 0.5f };
        _tree.DrawString(font, new Vector2(locationX, textYPos), locationText, HorizontalAlignment.Left, -1, fontSize, locationColor);

        // Draw file name on the right side, after the location
        var fileNameX = locationX + locationWidth + spacing;
        var fileNameColor = textColor with { A = 0.7f };
        _tree.DrawString(font, new Vector2(fileNameX, textYPos), fileName, HorizontalAlignment.Left, -1, fontSize, fileNameColor);
    }

    /// Called while staging, off the UI thread - only touch diagItem here
    private void ConfigureDiagnosticTreeItem(TreeItem diagItem, SharpIdeDiagnostic sharpIdeDiagnostic)
    {
        diagItem.SetCellMode(0, TreeItem.TreeCellMode.Custom);
        diagItem.SetCustomAsButton(0, true);
        diagItem.SetTooltipText(0, sharpIdeDiagnostic.Message);
        diagItem.SharpIdeDiagnostic = sharpIdeDiagnostic;
        // Avoid allocation via Callable.From((TreeItem s, Rect2 x) => CustomDraw(s, x))
        diagItem.SetCustomDrawCallback(0, _diagnosticCustomDrawCallable!.Value);
    }

    [RequiresGodotUiThread]
    private void FreeTreeItem(TreeItem? treeItem)
    {
        // May already be freed, if its parent (project) item was removed first, which frees its children
        if (treeItem is null || IsInstanceValid(treeItem) is false) return;
        treeItem.SharpIdeDisposable?.Dispose();
        treeItem.Free();
    }

    private void TreeOnItemActivated()
    {
        var selected = _tree.GetSelected();
        var diagnostic = selected.SharpIdeDiagnostic;
        if (diagnostic is null) return;
        OpenDocumentContainingDiagnostic(diagnostic);
    }

    private void OpenDocumentContainingDiagnostic(SharpIdeDiagnostic diagnostic)
    {
        var file = _solution!.AllFiles[diagnostic.FilePath];
        var linePosition = new SharpIdeFileLinePosition(diagnostic.Span.Start.Line, diagnostic.Span.Start.Character);
        GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(file, linePosition);
    }
}
