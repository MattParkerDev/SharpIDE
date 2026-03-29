using System.Collections.Specialized;
using Godot;
using Microsoft.CodeAnalysis;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
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
    // TODO: Use observable collections in the solution model and downwards
    private readonly ObservableHashSet<SharpIdeProjectModel> _projects = [];

    public override void _Ready()
    {
        _diagnosticCustomDrawCallable = new Callable(this, MethodName.DiagnosticCustomDraw);
        _tree = GetNode<Tree>("%Tree");
        _tree.ItemActivated += TreeOnItemActivated;
        _rootItem = _tree.CreateItem();
        _rootItem.SetText(0, "Problems");
        BindToTree(_projects);
        _ = Task.GodotRun(AsyncReady);
    }

    private async Task AsyncReady()
    {
        await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
        _solution = _sharpIdeSolutionAccessor.SolutionModel;
        _projects.AddRange(_solution!.AllProjects);
    }

    public void BindToTree(ObservableHashSet<SharpIdeProjectModel> list)
    {
        var view = list.CreateView(y => new TreeItemContainer());
        view.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
            .SubscribeAwait(async (e, ct) => await (e.Action switch
            {
                NotifyCollectionChangedAction.Add => CreateProjectTreeItem(_tree, _rootItem, e),
                NotifyCollectionChangedAction.Remove => this.InvokeAsync(() => FreeTreeItem(e.OldItem.View.Value)),
                _ => Task.CompletedTask
            }), configureAwait: false).AddTo(this);
    }

    private async Task CreateProjectTreeItem(Tree tree, TreeItem parent, ViewChangedEvent<SharpIdeProjectModel, TreeItemContainer> e)
    {
        await this.InvokeAsync(() =>
        {
            var treeItem = tree.CreateItem(parent);
            treeItem.SetText(0, e.NewItem.Value.Name.Value);
            treeItem.SetIcon(0, CsprojIcon);
            treeItem.Visible = e.NewItem.Value.Diagnostics.Count is not 0;
            e.NewItem.View.Value = treeItem;
            
            var projectDiagnosticsView = e.NewItem.Value.Diagnostics.CreateView(y => new TreeItemContainer());
            projectDiagnosticsView.ObserveChanged().SubscribeOnThreadPool().ObserveOnThreadPool()
                .SubscribeAwait(async (innerEvent, ct) =>
                {
                    if (innerEvent.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)) return;
                    await this.InvokeAsync(() =>
                    {
                        treeItem.Visible = e.NewItem.Value.Diagnostics.Count is not 0;
                        switch (innerEvent.Action)
                        {
                            case NotifyCollectionChangedAction.Add: CreateDiagnosticTreeItem(_tree, treeItem, innerEvent); break;
                            case NotifyCollectionChangedAction.Remove: FreeTreeItem(innerEvent.OldItem.View.Value); break;
                        }
                    });
                }, configureAwait: false).AddTo(this);
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
        
        var message = diagnostic.Diagnostic.GetMessage();
        var severity = diagnostic.Diagnostic.Severity;
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

    [RequiresGodotUiThread]
    private void CreateDiagnosticTreeItem(Tree tree, TreeItem parent, ViewChangedEvent<SharpIdeDiagnostic, TreeItemContainer> e)
    {
        var diagItem = tree.CreateItem(parent);
        diagItem.SetCellMode(0, TreeItem.TreeCellMode.Custom);
        diagItem.SetCustomAsButton(0, true);
        diagItem.SetTooltipText(0, e.NewItem.Value.Diagnostic.GetMessage());
        diagItem.SharpIdeDiagnostic = e.NewItem.Value;
        // Avoid allocation via Callable.From((TreeItem s, Rect2 x) => CustomDraw(s, x))
        diagItem.SetCustomDrawCallback(0, _diagnosticCustomDrawCallable!.Value);
        e.NewItem.View.Value = diagItem;
    }
    
    [RequiresGodotUiThread]
    private void FreeTreeItem(TreeItem? treeItem)
    {
        treeItem?.Free();
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