using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.ExternalAccess.Pythia.Api;
using Microsoft.CodeAnalysis.Host.Mef;

namespace SharpIDE.Application.Features.Analysis;

[Export(typeof(IPythiaSignatureHelpProviderImplementation)), Shared]
public class PythiaStub : IPythiaSignatureHelpProviderImplementation
{
	[ImportingConstructor]
	[Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
	public PythiaStub()
	{
	}

	public async Task<(ImmutableArray<PythiaSignatureHelpItemWrapper> items, int? selectedItemIndex)> GetMethodGroupItemsAndSelectionAsync(ImmutableArray<IMethodSymbol> accessibleMethods, Document document,
		InvocationExpressionSyntax invocationExpression, SemanticModel semanticModel, SymbolInfo currentSymbol,
		CancellationToken cancellationToken)
	{
		//throw new NotImplementedException();
		return ([], null);
	}
}
