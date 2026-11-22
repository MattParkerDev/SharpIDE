using Microsoft.CodeAnalysis;

namespace SharpIDE.Application.Features.Analysis;

/// Total is 0 when the amount of work isn't known
public readonly record struct CodeActionProgressSnapshot(string? Description, int Completed, int Total, bool IsApplying);

/// Reported from any thread - the UI polls Snapshot
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

	/// Clear starts a new phase, e.g. fixing, then cleaning up the fixed documents
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
