using Microsoft.CodeAnalysis.SignatureHelp;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

public class SharpIdeSignatureHelpItems
{
	public required SignatureHelpItems Items { get; init; }
	public required LinePositionSpan ApplicableSpan { get; init; }
}
