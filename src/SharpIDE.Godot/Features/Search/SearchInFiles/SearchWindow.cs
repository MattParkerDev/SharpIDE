using Godot;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.Search;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Search;

public partial class SearchWindow : PopupPanel
{
    private Label _resultCountLabel = null!;
    private LineEdit _lineEdit = null!;
    private VBoxContainer _searchResultsContainer = null!;
    public SharpIdeSolutionModel Solution { get; set; } = null!;
	private readonly PackedScene _searchResultEntryScene = ResourceLoader.Load<PackedScene>("res://Features/Search/SearchInFiles/SearchResultComponent.tscn");

    private CancellationTokenSource _cancellationTokenSource = new();
    private AsyncBatchingWorkQueue<FindInFilesSearchResult> _newFileResultToDisplayQueue = null!;
    private int _resultCount;
    private bool _isNewSearch;

    [Inject] private readonly SearchService _searchService = null!;
    
    public override void _Ready()
    {
        _resultCountLabel = GetNode<Label>("%ResultCountLabel");
        _resultCountLabel.Text = "";
        _lineEdit = GetNode<LineEdit>("%SearchLineEdit");
        _lineEdit.Text = "";
        _searchResultsContainer = GetNode<VBoxContainer>("%SearchResultsVBoxContainer");
        _searchResultsContainer.GetChildren().ToList().ForEach(s => s.QueueFree());
        _newFileResultToDisplayQueue = new AsyncBatchingWorkQueue<FindInFilesSearchResult>(TimeSpan.FromMilliseconds(50), RenderBatchAsync, IAsynchronousOperationListener.Instance, CancellationToken.None);
        _lineEdit.TextChanged += OnTextChanged;
        AboutToPopup += OnAboutToPopup;
    }

    public void SetSearchText(string searchText)
    {
        _lineEdit.Text = searchText;
    }

    private void OnAboutToPopup()
    {
        _lineEdit.SelectAll();
        Callable.From(_lineEdit.GrabFocus).CallDeferred();

        if (string.IsNullOrEmpty(_lineEdit.Text))
        {
            return;
        }
        
        BeginSearch(_lineEdit.Text);
    }

    private void OnTextChanged(string newText)
    {
        BeginSearch(newText);
    }
    
    private void BeginSearch(string searchText)
    {
        _resultCount = 0;
        _cancellationTokenSource.Cancel();
        // TODO: Investigate allocations
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _isNewSearch = true;
        _ = Task.GodotRun(() => Search(searchText, token));
    }

    private async ValueTask RenderBatchAsync(ImmutableSegmentedList<FindInFilesSearchResult> batch, CancellationToken cancellationToken)
    {
        await this.InvokeAsync(async () =>
        {
            if (_isNewSearch)
            {
                // Delay removing old results until the first batch of new results is ready, to reduce UI flickering
                _searchResultsContainer.QueueFreeChildren();
                _resultCountLabel.Text = string.Empty;
                _isNewSearch = false;
            }
            foreach (var searchResult in batch)
            {
                var resultNode = _searchResultEntryScene.Instantiate<SearchResultComponent>();
                resultNode.Result = searchResult;
                resultNode.ParentSearchWindow = this;
                _searchResultsContainer.AddChild(resultNode);
                _resultCount++;
            }
            _resultCountLabel.Text = $"{_resultCount} file(s) found";
        });
    }

    private async Task Search(string text, CancellationToken cancellationToken)
    {
        var (asyncEnumerable, searchResult) = _searchService.FindInFiles(Solution, text, cancellationToken);
        if (searchResult == SearchResult.InvalidSearch)
        {
            await this.InvokeAsync(() =>
            {
                _searchResultsContainer.QueueFreeChildren();
                _resultCountLabel.Text = string.Empty;
            });
            return;
        }
        await foreach (var item in asyncEnumerable.WithCancellation(cancellationToken))
        {
            _newFileResultToDisplayQueue.AddWork(item);
        }

        if (_resultCount is 0)
        {
            // no items would have been added to the render queue, so we need to clear old results and show "0 files found" message here
            await this.InvokeAsync(() =>
            {
                _searchResultsContainer.QueueFreeChildren();
                _resultCountLabel.Text = "0 file(s) found";
             });
        }
    }
}
