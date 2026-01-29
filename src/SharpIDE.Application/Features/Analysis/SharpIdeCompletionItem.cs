using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

public readonly record struct SharpIdeCompletionItem(CompletionItem CompletionItem, ImmutableArray<TextSpan>? MatchedSpans)
{
	public readonly CompletionItem CompletionItem = CompletionItem;
	public readonly ImmutableArray<TextSpan>? MatchedSpans = MatchedSpans;
}
