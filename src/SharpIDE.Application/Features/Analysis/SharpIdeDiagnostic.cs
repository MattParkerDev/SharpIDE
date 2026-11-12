using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

/// A snapshot of a Roslyn Diagnostic. Deliberately does not keep the Diagnostic itself:
/// - its message arguments can be symbols, which hold their whole Compilation alive. Project diagnostics are long-lived (kept across many
///   compilations when unchanged), so holding the Diagnostic would pin outdated Compilations in memory
/// - the message is formatted once here, rather than on every draw/access
/// Equality is plain value equality over these fields, which is what lets project diagnostics be updated as a minimal diff.
public sealed record SharpIdeDiagnostic
{
	public SharpIdeDiagnostic(LinePositionSpan span, Diagnostic diagnostic, string filePath)
	{
		Span = span;
		FilePath = filePath;
		Id = diagnostic.Id;
		Severity = diagnostic.Severity;
		Message = diagnostic.GetMessage();
	}

	public LinePositionSpan Span { get; }
	public string FilePath { get; }
	public string Id { get; }
	public DiagnosticSeverity Severity { get; }
	public string Message { get; }
}
