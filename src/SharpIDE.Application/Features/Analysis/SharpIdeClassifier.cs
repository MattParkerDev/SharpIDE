using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace SharpIDE.Application.Features.Analysis;

/// <summary>A classified range in a document's own coordinates. May span multiple lines.</summary>
public readonly record struct SharpIdeClassifiedTextSpan(TextSpan TextSpan, SharpIdeClassification Classification, SharpIdeClassificationModifiers Modifiers);

/// <summary>
/// Shared C# classification, mirroring Roslyn's LSP semantic tokens (SemanticTokensHelpers.ComputeSemanticTokensDataAsync).
/// </summary>
public static class SharpIdeClassifier
{
	private static readonly ConcurrentDictionary<string, byte> _reportedUnknownClassifications = new(StringComparer.Ordinal);

	/// <summary>
	/// Classifies <paramref name="spans"/> (sorted, non-intersecting) of <paramref name="document"/>, merging syntactic, semantic and embedded language classifications.
	/// The result is sorted and non-overlapping, with additive classifications (e.g. static) folded into <see cref="SharpIdeClassifiedTextSpan.Modifiers"/>.
	/// Unclassified regions ('text') are omitted.
	/// </summary>
	public static async Task<List<SharpIdeClassifiedTextSpan>> GetClassifiedSpansAsync(Document document, ImmutableArray<TextSpan> spans, ILogger? logger, CancellationToken cancellationToken)
	{
		var result = new List<SharpIdeClassifiedTextSpan>();
		if (spans.IsEmpty) return result;

		using var _ = Classifier.GetPooledList(out var classifiedSpans);
		await ClassifierHelper.AddClassifiedSpansAsync(classifiedSpans, document, spans, ClassificationOptions.Default, includeAdditiveSpans: true, cancellationToken);

		// Spans are sorted by start only, so additive spans may come before or after the span they decorate
		classifiedSpans.Sort(static (a, b) => a.TextSpan.CompareTo(b.TextSpan));

		var index = 0;
		while (index < classifiedSpans.Count)
		{
			var textSpan = classifiedSpans[index].TextSpan;
			var classification = SharpIdeClassification.Text;
			var modifiers = SharpIdeClassificationModifiers.None;

			// Multiple classifications for the same span are combined into one: the main classification plus modifiers
			for (; index < classifiedSpans.Count && classifiedSpans[index].TextSpan == textSpan; index++)
			{
				var classificationTypeName = classifiedSpans[index].ClassificationType;
				if (SharpIdeClassifications.TryGetModifier(classificationTypeName, out var modifier))
				{
					modifiers |= modifier;
					continue;
				}

				classification = SharpIdeClassifications.FromName(classificationTypeName);
				if (classification is SharpIdeClassification.Unknown && _reportedUnknownClassifications.TryAdd(classificationTypeName, 0))
				{
					logger?.LogWarning("Unhandled classification type '{ClassificationType}'", classificationTypeName);
				}
			}

			if (textSpan.IsEmpty || classification is SharpIdeClassification.Text) continue;
			result.Add(new SharpIdeClassifiedTextSpan(textSpan, classification, modifiers));
		}

		return result;
	}

	/// <summary>
	/// Adds <paramref name="span"/> to <paramref name="result"/> as one <see cref="SharpIdeClassifiedSpan"/> per line it covers, excluding line breaks.
	/// </summary>
	public static void AddSingleLineSpans(SourceText text, SharpIdeClassifiedTextSpan span, List<SharpIdeClassifiedSpan> result)
	{
		var lines = text.Lines;
		var startLine = lines.GetLineFromPosition(span.TextSpan.Start).LineNumber;
		var endLine = lines.GetLineFromPosition(span.TextSpan.End).LineNumber;

		for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
		{
			var line = lines[lineNumber];
			var start = Math.Max(span.TextSpan.Start, line.Start);
			var end = Math.Min(span.TextSpan.End, line.End);
			if (end <= start) continue;

			result.Add(new SharpIdeClassifiedSpan(lineNumber, start - line.Start, end - start, span.Classification, span.Modifiers));
		}
	}

	public static ImmutableArray<SharpIdeClassifiedSpan> ToSingleLineSpans(SourceText text, IEnumerable<SharpIdeClassifiedTextSpan> spans)
	{
		var result = new List<SharpIdeClassifiedSpan>();
		foreach (var span in spans)
		{
			AddSingleLineSpans(text, span, result);
		}
		return [..result];
	}
}
