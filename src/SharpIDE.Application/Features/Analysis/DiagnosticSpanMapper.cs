using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

/// Moves diagnostics through text edits, so squiggles stay on the code they belong to while fresh diagnostics are computed,
/// and so diagnostics computed against an older text can be shown on the current one.
public static class DiagnosticSpanMapper
{
	/// Maps diagnostics computed against <paramref name="oldText"/> onto <paramref name="newText"/>.
	/// Text inserted before a diagnostic moves it, text typed inside it stretches it, and a diagnostic whose text is deleted is dropped.
	/// <paramref name="newText"/> must descend from <paramref name="oldText"/> via WithChanges - if not (or Roslyn no longer knows the changes), the diagnostics are returned as they are.
	public static ImmutableArray<SharpIdeDiagnostic> MapForward(ImmutableArray<SharpIdeDiagnostic> diagnostics, SourceText oldText, SourceText newText)
	{
		if (diagnostics.IsDefaultOrEmpty || ReferenceEquals(oldText, newText)) return diagnostics;

		var changes = newText.GetChangeRanges(oldText); // Ordered, non-overlapping, in oldText's coordinates
		if (changes.Count is 0) return diagnostics;
		// Unrelated texts are reported as one change replacing everything - mapping through that would drop every diagnostic
		if (changes is [var only] && only.Span == new TextSpan(0, oldText.Length) && only.NewLength == newText.Length) return diagnostics;

		var builder = ImmutableArray.CreateBuilder<SharpIdeDiagnostic>(diagnostics.Length);
		foreach (var diagnostic in diagnostics)
		{
			if (TryMap(diagnostic.Span, oldText, newText, changes, out var mappedSpan) is false) continue;
			var mappedFadeSpans = MapFadeSpans(diagnostic.FadeSpans, oldText, newText, changes);
			if (mappedSpan == diagnostic.Span && mappedFadeSpans == diagnostic.FadeSpans)
			{
				builder.Add(diagnostic);
				continue;
			}
			builder.Add(diagnostic with { Span = mappedSpan, FadeSpans = mappedFadeSpans });
		}
		return builder.DrainToImmutable();
	}

	/// Returns the same array if nothing moved, so unchanged diagnostics are not reallocated on every keystroke
	private static ImmutableArray<LinePositionSpan> MapFadeSpans(ImmutableArray<LinePositionSpan> fadeSpans, SourceText oldText, SourceText newText, IReadOnlyList<TextChangeRange> changes)
	{
		if (fadeSpans.IsDefaultOrEmpty) return fadeSpans;
		ImmutableArray<LinePositionSpan>.Builder? builder = null;
		for (var i = 0; i < fadeSpans.Length; i++)
		{
			var mapped = TryMap(fadeSpans[i], oldText, newText, changes, out var mappedSpan);
			if (builder is null && mapped && mappedSpan == fadeSpans[i]) continue;
			if (builder is null)
			{
				builder = ImmutableArray.CreateBuilder<LinePositionSpan>(fadeSpans.Length);
				builder.AddRange(fadeSpans, i);
			}
			if (mapped) builder.Add(mappedSpan);
		}
		return builder?.ToImmutable() ?? fadeSpans;
	}

	private static bool TryMap(LinePositionSpan span, SourceText oldText, SourceText newText, IReadOnlyList<TextChangeRange> changes, out LinePositionSpan mappedSpan)
	{
		mappedSpan = span;
		var lines = oldText.Lines;
		if (span.Start.Line >= lines.Count || span.End.Line >= lines.Count) return false;
		var oldSpan = lines.GetTextSpan(span);
		if (oldSpan.End > oldText.Length) return false;

		var newStart = MapPosition(oldSpan.Start, isStart: true, changes);
		var newEnd = MapPosition(oldSpan.End, isStart: false, changes);
		if (newEnd < newStart || newEnd > newText.Length) return false;
		// Its text was deleted
		if (newEnd == newStart && oldSpan.IsEmpty is false) return false;

		mappedSpan = newText.Lines.GetLinePositionSpan(TextSpan.FromBounds(newStart, newEnd));
		return true;
	}

	/// A start sticks to the text after it - typing at the start of a squiggle pushes it along, rather than extending it.
	/// An end sticks to the text before it - typing at the end of a squiggle does not extend it.
	private static int MapPosition(int position, bool isStart, IReadOnlyList<TextChangeRange> changes)
	{
		var delta = 0;
		foreach (var change in changes)
		{
			var isBefore = change.Span.End < position || (change.Span.End == position && (isStart || change.Span.IsEmpty is false));
			if (isBefore)
			{
				delta += change.NewLength - change.Span.Length;
				continue;
			}
			// The position was inside replaced text
			if (change.Span.Start < position) return isStart ? change.Span.Start + delta + change.NewLength : change.Span.Start + delta;
			break;
		}
		return position + delta;
	}
}
