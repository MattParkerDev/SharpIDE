using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

/// A snapshot of a Roslyn Diagnostic. Deliberately does not keep the Diagnostic itself:
/// - its message arguments can be symbols, which hold their whole Compilation alive. Project diagnostics are long-lived (kept across many
///   compilations when unchanged), so holding the Diagnostic would pin outdated Compilations in memory
/// - the message is formatted once here, rather than on every draw/access
/// Equality is value equality over these fields (FadeSpans by sequence), which is what lets project diagnostics be updated as a minimal diff.
public sealed record SharpIdeDiagnostic
{
	public SharpIdeDiagnostic(LinePositionSpan span, Diagnostic diagnostic, string filePath)
	{
		Span = span;
		FilePath = filePath;
		Id = diagnostic.Id;
		Severity = diagnostic.Severity;
		Message = diagnostic.GetMessage();
		FadeSpans = GetFadeSpans(span, diagnostic);
		IsUnresolvedSymbol = GetIsUnresolvedSymbol(diagnostic);
	}

	/// init, so the editor can move a diagnostic as text is edited, before fresh diagnostics arrive
	public LinePositionSpan Span { get; init; }
	public string FilePath { get; }
	public string Id { get; }
	public DiagnosticSeverity Severity { get; }
	public string Message { get; }
	/// The text to fade out, for diagnostics that mark code as unnecessary (e.g. unnecessary usings, IDE0005). Empty for other diagnostics.
	public ImmutableArray<LinePositionSpan> FadeSpans { get; init; }
	/// Whether this is a compiler error for a name that could not be resolved (e.g. CS0246, type or namespace not found).
	/// The editor shows these by colouring the name red, as Rider does, rather than squiggling it.
	/// Contextual keywords are excluded - while typing e.g. `var` on its own line, it is (correctly) reported as an unknown name, but colouring a keyword red looks wrong.
	public bool IsUnresolvedSymbol { get; }

	private static bool GetIsUnresolvedSymbol(Diagnostic diagnostic)
	{
		if (UnresolvedSymbolIds.Contains(diagnostic.Id) is false) return false;
		var location = diagnostic.Location;
		if (location.SourceTree is not { } tree || tree.TryGetText(out var text) is false) return true;
		var name = text.ToString(location.SourceSpan);
		return SyntaxFacts.GetContextualKeywordKind(name) is SyntaxKind.None && KeywordLikeTypeNames.Contains(name) is false;
	}

	/// Names that are used like keywords, but that Roslyn doesn't consider contextual keywords
	private static readonly FrozenSet<string> KeywordLikeTypeNames = FrozenSet.Create("dynamic", "nint", "nuint");

	private static readonly FrozenSet<string> UnresolvedSymbolIds = FrozenSet.Create(
		"CS0103", // The name 'x' does not exist in the current context
		"CS0117", // 'Type' does not contain a definition for 'x'
		"CS0234", // The type or namespace name 'x' does not exist in the namespace 'Ns'
		"CS0246", // The type or namespace name 'x' could not be found
		"CS0426", // The type name 'x' does not exist in the type 'Type'
		"CS1061"  // 'Type' does not contain a definition for 'x' and no accessible extension method 'x' ...
	);

	/// Hidden diagnostics are not shown, except for those that mark code as unnecessary, as their fading is visible - the same as the Roslyn LSP.
	/// Roslyn also creates unnecessary diagnostics with no message (e.g. for remove unnecessary usings) that are not meant to be shown at all.
	public static bool ShouldShowInEditor(Diagnostic diagnostic)
	{
		if (diagnostic.Severity is not DiagnosticSeverity.Hidden) return true;
		return IsUnnecessary(diagnostic) && string.IsNullOrEmpty(diagnostic.GetMessage()) is false;
	}

	private static bool IsUnnecessary(Diagnostic diagnostic) => diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary);

	/// Roslyn's analyzers can mark specific additional locations to fade, via a property listing their indices (e.g. "[1,2]"). Otherwise the whole diagnostic is faded.
	/// See DiagnosticHelper.CreateWithLocationTags and the Roslyn LSP's ProtocolConversions.ConvertDiagnostic
	private static ImmutableArray<LinePositionSpan> GetFadeSpans(LinePositionSpan span, Diagnostic diagnostic)
	{
		if (IsUnnecessary(diagnostic) is false) return [];
		if (diagnostic.Properties.TryGetValue(WellKnownDiagnosticTags.Unnecessary, out var indices) is false || indices is not ['[', .., ']']) return [span];

		var builder = ImmutableArray.CreateBuilder<LinePositionSpan>();
		foreach (var indexText in indices.AsSpan(1, indices.Length - 2).ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (int.TryParse(indexText, out var index) is false || index < 0 || index >= diagnostic.AdditionalLocations.Count) continue;
			var location = diagnostic.AdditionalLocations[index];
			if (location.IsInSource is false || location.SourceTree != diagnostic.Location.SourceTree) continue;
			builder.Add(location.GetMappedLineSpan().Span);
		}
		return builder.ToImmutable();
	}

	public bool Equals(SharpIdeDiagnostic? other)
	{
		if (other is null) return false;
		if (ReferenceEquals(this, other)) return true;
		return Span == other.Span && FilePath == other.FilePath && Id == other.Id && Severity == other.Severity && Message == other.Message
			   && FadeSpans.AsSpan().SequenceEqual(other.FadeSpans.AsSpan());
	}

	public override int GetHashCode() => HashCode.Combine(Span, FilePath, Id, Severity, Message, FadeSpans.Length);
}
