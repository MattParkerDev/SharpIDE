using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Editor;

/// A change to a file, computed against <see cref="OldText"/> (e.g. by a code action or rename).
public sealed record SharpIdeFileTextEdit(SharpIdeFile File, SourceText OldText, ImmutableArray<TextChange> Changes);

/// A request for the editor of an open file to change its text.
/// The editor owns an open file's text, so every change to it goes through the editor, which reports the result back like any other edit.
public sealed record SharpIdeFileEditRequest
{
	/// The file changed on disk. Disk always wins: the editor replaces its text, applied as a minimal edit.
	public string? ReloadedText { get; private init; }

	/// The changes are relative to this text. If the editor's text no longer matches it, the request is rejected.
	public SourceText? BaseText { get; private init; }
	public ImmutableArray<TextChange> Changes { get; private init; } = [];

	public static SharpIdeFileEditRequest Reload(string reloadedText) => new() { ReloadedText = reloadedText };
	public static SharpIdeFileEditRequest ApplyChanges(SourceText baseText, ImmutableArray<TextChange> changes) => new() { BaseText = baseText, Changes = changes };
}

public static class TextDiff
{
	/// The smallest single change that turns <paramref name="oldText"/> into <paramref name="newText"/> (common prefix and suffix), or null if they are equal.
	public static TextChange? GetMinimalChange(string oldText, string newText)
	{
		var oldSpan = oldText.AsSpan();
		var newSpan = newText.AsSpan();
		var prefix = oldSpan.CommonPrefixLength(newSpan);
		if (prefix == oldSpan.Length && prefix == newSpan.Length) return null;

		var maxSuffix = Math.Min(oldSpan.Length, newSpan.Length) - prefix;
		var suffix = 0;
		const int blockSize = 64;
		// Compare in vectorised blocks first, then finish character by character
		while (suffix + blockSize <= maxSuffix &&
		       oldSpan.Slice(oldSpan.Length - suffix - blockSize, blockSize).SequenceEqual(newSpan.Slice(newSpan.Length - suffix - blockSize, blockSize)))
		{
			suffix += blockSize;
		}
		while (suffix < maxSuffix && oldSpan[oldSpan.Length - 1 - suffix] == newSpan[newSpan.Length - 1 - suffix])
		{
			suffix++;
		}

		var oldLength = oldSpan.Length - prefix - suffix;
		var newLength = newSpan.Length - prefix - suffix;
		return new TextChange(new TextSpan(prefix, oldLength), newText.Substring(prefix, newLength));
	}

	/// Maps <paramref name="change"/>, computed against <paramref name="oldText"/>, onto <paramref name="currentText"/>, like an edge-inclusive tracking span.
	/// Text typed inside the span or at its end since (e.g. completion filter text) is replaced too.
	/// Returns false if an edit since straddles the start or end of the span, as the intent is then unclear.
	/// <paramref name="currentText"/> must descend from <paramref name="oldText"/> via WithChanges, otherwise the whole document counts as changed.
	public static bool TryMapChangeForward(SourceText oldText, SourceText currentText, TextChange change, out TextChange mappedChange)
	{
		mappedChange = change;
		if (ReferenceEquals(oldText, currentText)) return true;

		var oldStart = change.Span.Start;
		var oldEnd = change.Span.End;
		var startDelta = 0;
		var endDelta = 0;
		foreach (var range in currentText.GetChangeRanges(oldText)) // Ordered, non-overlapping, in oldText's coordinates
		{
			var delta = range.NewLength - range.Span.Length;
			// An insertion at the start of a non-empty span is before it. For an empty span (e.g. completion invoked before typing), it extends it.
			var isBefore = range.Span.End < oldStart || (range.Span.End == oldStart && (range.Span.IsEmpty is false || change.Span.IsEmpty is false));
			if (isBefore)
			{
				startDelta += delta;
				endDelta += delta;
			}
			else if (range.Span.Start >= oldStart && range.Span.End <= oldEnd)
			{
				endDelta += delta;
			}
			else if (range.Span.Start > oldEnd)
			{
				break;
			}
			else
			{
				return false;
			}
		}

		var newStart = oldStart + startDelta;
		var newEnd = oldEnd + endDelta;
		if (newStart < 0 || newEnd < newStart || newEnd > currentText.Length) return false;
		mappedChange = new TextChange(TextSpan.FromBounds(newStart, newEnd), change.NewText!);
		return true;
	}
}
