using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

/// A document's diagnostics, and the text they were computed against, so the editor can map them onto any edits made since
public readonly record struct SharpIdeDocumentDiagnostics(ImmutableArray<SharpIdeDiagnostic> Diagnostics, SourceText? Text)
{
	public static SharpIdeDocumentDiagnostics Empty => new([], null);
}
