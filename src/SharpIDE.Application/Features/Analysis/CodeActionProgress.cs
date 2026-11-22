using Microsoft.CodeAnalysis;

namespace SharpIDE.Application.Features.Analysis;

/// A point-in-time view of a code action's progress. Total is 0 when the amount of work isn't known (an indeterminate progress bar).
/// IsApplying is whether the changes are being applied, rather than computed - they can no longer be cancelled.
public readonly record struct CodeActionProgressSnapshot(string? Description, int Completed, int Total, bool IsApplying);

/// Collects the progress Roslyn reports while computing a code action (e.g. fix all), and the progress of applying its changes.
/// Reported from any thread - the UI reads Snapshot when it redraws, rather than being called for every report.
public sealed class CodeActionProgress : IProgress<CodeAnalysisProgress>
{
	private readonly Lock _lock = new();
	private string? _description;
	private int _completed;
	private int _total;
	private bool _isApplying;

	public CodeActionProgressSnapshot Snapshot
	{
		get
		{
			lock (_lock) return new CodeActionProgressSnapshot(_description, Math.Min(_completed, _total), _total, _isApplying);
		}
	}

	/// Mirrors how VS's progress tracker interprets CodeAnalysisProgress - Clear restarts the bar for a new phase (e.g. fixing, then cleaning up the fixed documents)
	public void Report(CodeAnalysisProgress value)
	{
		lock (_lock)
		{
			if (value.ClearValue)
			{
				_completed = 0;
				_total = 0;
			}
			if (value.IncompleteItemsValue is { } incompleteItems) _total += incompleteItems;
			if (value.CompleteItemValue is { } completeItems) _completed += completeItems;
			if (value.DescriptionValue is { } description) _description = description;
		}
	}

	/// The changes have been computed, and are now being applied - with no known amount of work
	public void StartApplying(string description)
	{
		lock (_lock)
		{
			_isApplying = true;
			_description = description;
			_completed = 0;
			_total = 0;
		}
	}
}
