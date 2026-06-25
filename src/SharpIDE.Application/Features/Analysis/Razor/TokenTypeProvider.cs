using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis.LanguageServer.Handler.SemanticTokens;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;

namespace SharpIDE.Application.Features.Analysis.Razor;

public static class TokenTypeProvider
{
	public static string[] ConstructTokenTypes(bool supportsVsExtensions)
	{
		string[] types = [.. SemanticTokensSchema.GetSchema(supportsVsExtensions).AllTokenTypes, ..GetStaticFieldValues(typeof(SemanticTokenTypes))];
		//return new SemanticTokenTypes(types);
		return types;
	}

	public static string[] ConstructTokenModifiers()
	{
		string[] types = [.. SemanticTokensSchema.TokenModifiers, ..GetStaticFieldValues(typeof(SemanticTokenModifiers))];
		//return new SemanticTokenModifiers(types);
		return types;
	}

	private static ImmutableArray<string> GetStaticFieldValues(Type type)
	{
		var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Static).Select(s => s.GetValue(null)).OfType<string>().ToImmutableArray();
		return fields;
	}
}
