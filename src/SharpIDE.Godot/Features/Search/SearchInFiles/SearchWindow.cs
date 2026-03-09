using Godot;
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
    
    [Inject] private readonly SearchService _searchService = null!;
    
    public override void _Ready()
    {
        _resultCountLabel = GetNode<Label>("%ResultCountLabel");
        _resultCountLabel.Text = "";
        _lineEdit = GetNode<LineEdit>("%SearchLineEdit");
        _lineEdit.Text = "";
        _searchResultsContainer = GetNode<VBoxContainer>("%SearchResultsVBoxContainer");
        _searchResultsContainer.GetChildren().ToList().ForEach(s => s.QueueFree());
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
        _resultCountLabel.Text = string.Empty;
        foreach (var child in _searchResultsContainer.GetChildren())
        {
            child.QueueFree();
        }
        
        _cancellationTokenSource.Cancel();
        // TODO: Investigate allocations
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _ = Task.GodotRun(() => Search(searchText, token));
    }

    private async Task Search(string text, CancellationToken cancellationToken)
    {
        var resultCount = 0;

        await foreach (var searchResults in _searchService.FindInFiles(Solution, text, cancellationToken)
                                                          .Chunk(size: 10)
                                                          .WithCancellation(cancellationToken))
        {
            await this.InvokeAsync(async () =>
            {
                foreach (var searchResult in searchResults)
                {
                    var resultNode = _searchResultEntryScene.Instantiate<SearchResultComponent>();
                    resultNode.Result = searchResult;
                    resultNode.ParentSearchWindow = this;
                    _searchResultsContainer.AddChild(resultNode);

                    resultCount++;
                }
            });
        }

        await this.InvokeAsync(async () => { _resultCountLabel.Text = $"{resultCount} files(s) found"; });
    }
}
