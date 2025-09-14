extern alias WorkspaceAlias;
using RazorCodeDocumentExtensions = WorkspaceAlias::Microsoft.AspNetCore.Razor.Language.RazorCodeDocumentExtensions;

namespace SharpIDE.RazorAccess;

public record struct SharpIdeRazorClassifiedSpan(SharpIdeRazorSourceSpan Span, SharpIdeRazorSpanKind Kind, string? CodeClassificationType = null);

public enum SharpIdeRazorSpanKind
{
	Transition,
	MetaCode,
	Comment,
	Code,
	Markup,
	None,
}

public static class SharpIdeRazorClassifiedSpanExtensions
{
	public static SharpIdeRazorSpanKind ToSharpIdeSpanKind(this RazorCodeDocumentExtensions.SpanKind kind) => kind switch
	{
		RazorCodeDocumentExtensions.SpanKind.Transition => SharpIdeRazorSpanKind.Transition,
		RazorCodeDocumentExtensions.SpanKind.MetaCode => SharpIdeRazorSpanKind.MetaCode,
		RazorCodeDocumentExtensions.SpanKind.Comment => SharpIdeRazorSpanKind.Comment,
		RazorCodeDocumentExtensions.SpanKind.Code => SharpIdeRazorSpanKind.Code,
		RazorCodeDocumentExtensions.SpanKind.Markup => SharpIdeRazorSpanKind.Markup,
		RazorCodeDocumentExtensions.SpanKind.None => SharpIdeRazorSpanKind.None,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
	};
}
