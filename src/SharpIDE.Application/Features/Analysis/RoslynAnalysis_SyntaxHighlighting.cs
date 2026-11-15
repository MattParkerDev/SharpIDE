using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Remote.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.SolutionDiscovery;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
	/// <summary>
	/// Syntax highlighting for any supported document kind. Spans are sorted, single-line and non-overlapping.
	/// </summary>
	public async Task<ImmutableArray<SharpIdeClassifiedSpan>> GetSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetSyntaxHighlighting)}");
		await _solutionLoadedTcs.Task;
		if (fileModel.IsCsharpFile) return await GetCSharpDocumentSyntaxHighlighting(fileModel, cancellationToken);
		if (fileModel.IsRazorFile) return await GetRazorDocumentSyntaxHighlighting(fileModel, cancellationToken);
		return [];
	}

	private async Task<ImmutableArray<SharpIdeClassifiedSpan>> GetCSharpDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken)
	{
		var document = GetRequiredCSharpDocumentForSharpIdeFile(fileModel);

		var text = await document.GetTextAsync(cancellationToken);
		var classifiedSpans = await SharpIdeClassifier.GetClassifiedSpansAsync(document, [new TextSpan(0, text.Length)], _logger, cancellationToken);
		return SharpIdeClassifier.ToSingleLineSpans(text, classifiedSpans);
	}

	private readonly record struct RazorHighlightRange(TextSpan TextSpan, SharpIdeClassification Classification, SharpIdeClassificationModifiers Modifiers, bool FromRazor)
	{
		// Mirrors Razor's SemanticRange.CompareTo: when ranges start at the same position, Razor's range sorts first and wins,
		// e.g. the tag name in <My.Cool.Component> is one Razor range, but several C# ranges (namespaces and a type name)
		public static int Compare(RazorHighlightRange x, RazorHighlightRange y)
		{
			var result = x.TextSpan.Start.CompareTo(y.TextSpan.Start);
			if (result is not 0) return result;
			if (x.FromRazor != y.FromRazor) return x.FromRazor ? -1 : 1;
			return x.TextSpan.End.CompareTo(y.TextSpan.End);
		}
	}

	// Mirrors RazorSemanticTokensInfoService.GetSemanticTokensAsync
	private async Task<ImmutableArray<SharpIdeClassifiedSpan>> GetRazorDocumentSyntaxHighlighting(SharpIdeFile fileModel, CancellationToken cancellationToken)
	{
		var timer = Stopwatch.StartNew();
		var razorDocument = GetRequiredTextDocumentForSharpIdeFile(fileModel);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(razorDocument.Project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(razorDocument);

		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		var razorText = razorCodeDocument.Source.Text;

		var ranges = new List<RazorHighlightRange>();

		// Razor and HTML ranges
		var semanticRanges = new List<SemanticRange>();
		SemanticTokensVisitor.AddSemanticRanges(semanticRanges, razorCodeDocument, new TextSpan(0, razorText.Length), _semanticTokensLegendService!, false);
		var tokenTypes = _semanticTokensLegendService!.TokenTypes.All;
		foreach (var semanticRange in semanticRanges)
		{
			var classification = SharpIdeClassifications.FromName(tokenTypes[semanticRange.Kind]);
			if (classification is SharpIdeClassification.Unknown or SharpIdeClassification.Text) continue;
			ranges.Add(new RazorHighlightRange(razorText.GetTextSpan(semanticRange.AsLinePositionSpan()), classification, SharpIdeClassificationModifiers.None, FromRazor: true));
		}

		// C# ranges. Razor emits two C# documents: the impl document (markup/render tree, expressions etc.), and the
		// declaration document (e.g. @code block members). Each has its own source generated document, so classify both.
		await AddCSharpRanges(razorCodeDocument.GetRequiredCSharpDocument(false), await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken));

		var declCSharpDocument = razorCodeDocument.GetCSharpDocument(true);
		if (declCSharpDocument is not null && await razorProjectSnapshot.TryGetDeclGeneratedDocumentAsync(documentSnapshot, cancellationToken) is { } declGeneratedDocument)
		{
			await AddCSharpRanges(declCSharpDocument, declGeneratedDocument);
		}

		// Razor and C# ranges interleave, and can overlap. Keep the first range covering any position, which thanks to the sort order prefers Razor's.
		// This also removes the duplicates that come from generated C# being emitted several times, or mapping from both the impl and decl documents.
		ranges.Sort(RazorHighlightRange.Compare);
		var result = new List<SharpIdeClassifiedSpan>(ranges.Count);
		var coveredUpTo = 0;
		foreach (var range in ranges)
		{
			var textSpan = range.TextSpan;
			if (textSpan.End <= coveredUpTo) continue;
			if (textSpan.Start < coveredUpTo) textSpan = TextSpan.FromBounds(coveredUpTo, textSpan.End);
			SharpIdeClassifier.AddSingleLineSpans(razorText, new SharpIdeClassifiedTextSpan(textSpan, range.Classification, range.Modifiers), result);
			coveredUpTo = textSpan.End;
		}

		timer.Stop();
		_logger.LogInformation("RoslynAnalysis: Razor syntax highlighting for {FileName} took {ElapsedMilliseconds}ms", fileModel.Name.Value, timer.ElapsedMilliseconds);
		return [..result];

		async Task AddCSharpRanges(RazorCSharpDocument razorCSharpDocument, SourceGeneratedDocument generatedDocument)
		{
			var mappings = razorCSharpDocument.SourceMappingsSortedByGenerated;
			if (mappings.IsEmpty) return;

			// Only classify the generated C# that maps back to the razor document, not the generated scaffolding around it
			var generatedSpans = new List<TextSpan>(mappings.Length);
			foreach (var mapping in mappings)
			{
				var span = new TextSpan(mapping.GeneratedSpan.AbsoluteIndex, mapping.GeneratedSpan.Length);
				if (span.IsEmpty) continue;
				if (generatedSpans.Count > 0 && span.Start <= generatedSpans[^1].End)
				{
					generatedSpans[^1] = TextSpan.FromBounds(generatedSpans[^1].Start, Math.Max(generatedSpans[^1].End, span.End));
					continue;
				}
				generatedSpans.Add(span);
			}

			var classifiedSpans = await SharpIdeClassifier.GetClassifiedSpansAsync(generatedDocument, [..generatedSpans], _logger, cancellationToken);
			foreach (var classifiedSpan in classifiedSpans)
			{
				if (TryMapToRazorSpan(mappings, classifiedSpan.TextSpan) is not { } razorSpan) continue;
				ranges.Add(new RazorHighlightRange(razorSpan, classifiedSpan.Classification, classifiedSpan.Modifiers, FromRazor: false));
			}
		}

		// Strict mapping: the generated span must lie entirely within a single source mapping
		static TextSpan? TryMapToRazorSpan(ImmutableArray<SourceMapping> mappingsSortedByGenerated, TextSpan generatedSpan)
		{
			// Find the last mapping starting at or before the span
			int low = 0, high = mappingsSortedByGenerated.Length - 1, candidate = -1;
			while (low <= high)
			{
				var mid = low + (high - low) / 2;
				if (mappingsSortedByGenerated[mid].GeneratedSpan.AbsoluteIndex <= generatedSpan.Start)
				{
					candidate = mid;
					low = mid + 1;
				}
				else
				{
					high = mid - 1;
				}
			}
			if (candidate is -1) return null;

			var mapping = mappingsSortedByGenerated[candidate];
			var offset = generatedSpan.Start - mapping.GeneratedSpan.AbsoluteIndex;
			if (generatedSpan.End > mapping.GeneratedSpan.AbsoluteIndex + mapping.GeneratedSpan.Length) return null;
			if (offset + generatedSpan.Length > mapping.OriginalSpan.Length) return null;
			return new TextSpan(mapping.OriginalSpan.AbsoluteIndex + offset, generatedSpan.Length);
		}
	}
}
