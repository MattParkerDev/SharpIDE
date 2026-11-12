using System.Collections.Immutable;
using System.Text;
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
		var suffix = CommonSuffixLength(oldSpan[prefix..], newSpan[prefix..]);

		var oldLength = oldSpan.Length - prefix - suffix;
		var newLength = newSpan.Length - prefix - suffix;
		return new TextChange(new TextSpan(prefix, oldLength), newText.Substring(prefix, newLength));
	}

	/// As <see cref="GetMinimalChange(string, string)"/>, for UTF-32 text (e.g. Godot's native String), returning UTF-16 positions as Roslyn expects.
	/// Only the inserted text is converted to a managed string.
	/// Invalid code points (lone surrogates, > U+10FFFF) become U+FFFD, consistent with Encoding.UTF32, so positions match a full conversion.
	public static TextChange? GetMinimalChangeUtf32(ReadOnlySpan<uint> oldText, ReadOnlySpan<uint> newText)
	{
		var prefix = oldText.CommonPrefixLength(newText);
		if (prefix == oldText.Length && prefix == newText.Length) return null;
		var suffix = CommonSuffixLength(oldText[prefix..], newText[prefix..]);

		var removed = oldText.Slice(prefix, oldText.Length - prefix - suffix);
		var inserted = newText.Slice(prefix, newText.Length - prefix - suffix);
		// Code points above U+FFFF are a surrogate pair (2 chars) in UTF-16
		var start = prefix + CountSupplementaryCodePoints(oldText[..prefix]);
		var oldLength = removed.Length + CountSupplementaryCodePoints(removed);
		return new TextChange(new TextSpan(start, oldLength), Utf32ToUtf16(inserted));
	}

	private static int CommonSuffixLength<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : IEquatable<T>
	{
		var maxSuffix = Math.Min(a.Length, b.Length);
		var suffix = 0;
		const int blockSize = 64;
		// Compare in vectorised blocks first, then finish element by element
		while (suffix + blockSize <= maxSuffix &&
		       a.Slice(a.Length - suffix - blockSize, blockSize).SequenceEqual(b.Slice(b.Length - suffix - blockSize, blockSize)))
		{
			suffix += blockSize;
		}
		while (suffix < maxSuffix && a[a.Length - 1 - suffix].Equals(b[b.Length - 1 - suffix]))
		{
			suffix++;
		}
		return suffix;
	}

	private static int CountSupplementaryCodePoints(ReadOnlySpan<uint> text)
	{
		var count = 0;
		while (true) // Vectorised search, so text without any (the usual case) is a single fast scan
		{
			var index = text.IndexOfAnyInRange(0x10000u, 0x10FFFFu);
			if (index < 0) return count;
			count++;
			text = text[(index + 1)..];
		}
	}

	private static string Utf32ToUtf16(ReadOnlySpan<uint> text)
	{
		if (text.IsEmpty) return string.Empty;
		return string.Create(text.Length + CountSupplementaryCodePoints(text), text, static (destination, source) =>
		{
			var i = 0;
			foreach (var codePoint in source)
			{
				if (codePoint is >= 0x10000 and <= 0x10FFFF)
				{
					i += new Rune(codePoint).EncodeToUtf16(destination[i..]);
				}
				else
				{
					var c = codePoint <= 0xFFFF ? (char)codePoint : '�';
					destination[i++] = char.IsSurrogate(c) ? '�' : c;
				}
			}
		});
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
