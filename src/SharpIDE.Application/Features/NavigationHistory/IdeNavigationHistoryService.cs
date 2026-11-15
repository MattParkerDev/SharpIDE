using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.NavigationHistory;

public class IdeNavigationHistoryService
{
	private const int MaxHistorySize = 100;

	// Using LinkedList for back stack to efficiently remove oldest entries
	private readonly LinkedList<IdeNavigationLocation> _backStack = new();
	private readonly Stack<IdeNavigationLocation> _forwardStack = new();

	public bool CanGoBack => _backStack.Count > 0;
	public bool CanGoForward => _forwardStack.Count > 0;
	public ReactiveProperty<IdeNavigationLocation?> Current { get; private set; } = new(null);

	public bool EnableRecording { get; set; } = false;

	public void StartRecording() => EnableRecording = true;

	public void RecordNavigation(SharpIdeFile file, SharpIdeFileLinePosition linePosition)
	{
		if (EnableRecording is false) return;
		var location = new IdeNavigationLocation(file, linePosition);
		if (location == Current.Value)
		{
			// perhaps we filter out our forward and back navigations like this?
			return;
		}
		if (Current.Value is not null)
		{
			_backStack.AddLast(Current.Value);
			if (_backStack.Count > MaxHistorySize) _backStack.RemoveFirst();
		}
		Current.Value = location;
		_forwardStack.Clear();
	}

	public void GoBack()
	{
		if (!CanGoBack) throw new InvalidOperationException("Cannot go back, no history available.");
		if (Current.Value is not null)
		{
			_forwardStack.Push(Current.Value);
		}
		Current.Value = _backStack.Last();
		_backStack.RemoveLast();
	}

	public void GoForward()
	{
		if (!CanGoForward) throw new InvalidOperationException("Cannot go forward, no history available.");
		if (Current.Value is not null)
		{
			_backStack.AddLast(Current.Value);
		}

		Current.Value = _forwardStack.Pop();
	}
}

public record IdeNavigationLocation(SharpIdeFile File, SharpIdeFileLinePosition LinePosition);
