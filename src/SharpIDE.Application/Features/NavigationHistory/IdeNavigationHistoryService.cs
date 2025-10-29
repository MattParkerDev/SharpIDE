using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.NavigationHistory;

public class IdeNavigationHistoryService
{
	private readonly Stack<IdeNavigationLocation> _backStack = new();
	private IdeNavigationLocation? _current;
	private readonly Stack<IdeNavigationLocation> _forwardStack = new();

	public bool CanGoBack => _backStack.Count > 0;
	public bool CanGoForward => _forwardStack.Count > 0;
	public IdeNavigationLocation? Current => _current;

	public void RecordNavigation(SharpIdeFile file, SharpIdeFileLinePosition linePosition)
	{
		var location = new IdeNavigationLocation(file, linePosition);
		if (location == _current)
		{
			// perhaps we filter out our forward and back navigations like this?
			return;
		}
		if (_current is not null)
		{
			_backStack.Push(_current);
		}
		_current = location;
		_forwardStack.Clear();
	}

	public void GoBack()
	{
		if (!CanGoBack) throw new InvalidOperationException("Cannot go back, no history available.");
		if (_current is not null)
		{
			_forwardStack.Push(_current);
		}
		_current = _backStack.Pop();
		// TODO: Fire event
	}

	public void GoForward()
	{
		if (!CanGoForward) throw new InvalidOperationException("Cannot go forward, no history available.");
		if (_current is not null)
		{
			_backStack.Push(_current);
		}

		_current = _forwardStack.Pop();
		// TODO: Fire event
	}
}

public record IdeNavigationLocation(SharpIdeFile File, SharpIdeFileLinePosition LinePosition)
{
}
