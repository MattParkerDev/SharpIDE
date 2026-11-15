using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;

namespace SharpIDE.Application.Features.Analysis;

/// The solution level (host) analyzers - the compiler diagnostic analyzer, and the IDE analyzers (IDExxxx), which live in the Roslyn Features assemblies.
/// IDiagnosticAnalyzerService (and so ICodeFixService) only reports diagnostics from analyzers in the project's or the solution's analyzer references,
/// so without these, there are no code fixes for compiler errors or IDE diagnostics. The Roslyn LSP does the same in LanguageServerWorkspaceFactory.
/// These do not affect SharpIDE's own diagnostics, which only use each project's analyzer references.
public static class SolutionLevelAnalyzerReferences
{
	public static ImmutableArray<AnalyzerReference> References => field.IsDefault ? field = Create() : field;

	/// The Roslyn LSP uses AnalyzerFileReferences, but they need the assemblies' file paths, and under Godot, assemblies are loaded from a stream (so the files are not locked), so have no Location.
	/// The assemblies are already loaded anyway, so instantiate the analyzers from them directly.
	private static ImmutableArray<AnalyzerReference> Create()
	{
		// The compiler diagnostic analyzer (CSharpCompilerDiagnosticAnalyzer) is in the compiler assembly, the IDE analyzers are in the Features assemblies
		return MefHostServices.DefaultAssemblies
			.Append(typeof(CSharpCompilation).Assembly)
			.Distinct()
			.Select(assembly => (assembly, analyzers: LoadCSharpAnalyzers(assembly)))
			.Where(s => s.analyzers.Length is not 0)
			.Select(AnalyzerReference (s) => new AnalyzerImageReference(s.analyzers, display: s.assembly.GetName().Name))
			.ToImmutableArray();
	}

	private static ImmutableArray<DiagnosticAnalyzer> LoadCSharpAnalyzers(Assembly assembly)
	{
		return GetLoadableTypes(assembly)
			.Where(type => type is { IsAbstract: false, IsInterface: false, ContainsGenericParameters: false } && typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
			.Where(type => type.GetCustomAttribute<DiagnosticAnalyzerAttribute>(inherit: false)?.Languages.Contains(LanguageNames.CSharp) is true)
			.Select(CreateInstance)
			.OfType<DiagnosticAnalyzer>()
			.ToImmutableArray();

		static DiagnosticAnalyzer? CreateInstance(Type type)
		{
			try
			{
				return (DiagnosticAnalyzer?)Activator.CreateInstance(type);
			}
			catch
			{
				return null;
			}
		}
	}

	private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException e)
		{
			return e.Types.OfType<Type>();
		}
	}
}
