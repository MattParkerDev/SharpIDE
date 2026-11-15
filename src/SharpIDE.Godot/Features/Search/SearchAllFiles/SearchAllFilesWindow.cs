using Godot;
using SharpIDE.Application.Features.Search;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.Search.SearchAllFiles;

public partial class SearchAllFilesWindow : PopupPanel
{
	private Label _resultCountLabel = null!;
	private LineEdit _lineEdit = null!;
	private VBoxContainer _searchResultsContainer = null!;
	public SharpIdeSolutionModel Solution { get; set; } = null!;
	private readonly PackedScene _searchResultEntryScene = ResourceLoader.Load<PackedScene>("res://Features/Search/SearchAllFiles/SearchAllFilesResultComponent.tscn");

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

	private void OnAboutToPopup()
	{
		_lineEdit.SelectAll();
		Callable.From(_lineEdit.GrabFocus).CallDeferred();
	}

	private async void OnTextChanged(string newText)
	{
		await _cancellationTokenSource.CancelAsync();
		// TODO: Investigate allocations
		_cancellationTokenSource = new CancellationTokenSource();
		var token = _cancellationTokenSource.Token;
		await Task.GodotRun(() => Search(newText, token));
	}

	private async Task Search(string text, CancellationToken cancellationToken)
	{
		var result = await _searchService.FindFiles(Solution, text, cancellationToken);
		if (cancellationToken.IsCancellationRequested) return;
		await this.InvokeAsync(() =>
		{
			_searchResultsContainer.GetChildren().ToList().ForEach(s => s.QueueFree());
			foreach (var searchResult in result)
			{
				var resultNode = _searchResultEntryScene.Instantiate<SearchAllFilesResultComponent>();
				resultNode.Result = searchResult;
				resultNode.ParentSearchAllFilesWindow = this;
				_searchResultsContainer.AddChild(resultNode);
			}
			_resultCountLabel.Text = $"{result.Count} files(s) found";
		});
	}
}
