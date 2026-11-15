using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.Extensions.Logging;
using Roslyn.Utilities;

namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
	// https://github.com/dotnet/roslyn/blob/a5ff3f7bcb8cb7116709009e7bbafc73ce2d4c79/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/LanguageServerWorkspaceFactory.cs#L81
	public ImmutableArray<AnalyzerFileReference> CreateSolutionLevelAnalyzerReferencesForWorkspace(Workspace workspace)
	{
		var solutionLevelAnalyzerPaths = new DirectoryInfo(AppContext.BaseDirectory).GetFiles("*.dll")
			.Where(f => f.Name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal) && !f.Name.Contains("LanguageServer", StringComparison.Ordinal))
			.Select(f => f.FullName)
			.ToImmutableArray();

		var references = ImmutableArray.CreateBuilder<AnalyzerFileReference>();
		var loaderProvider = workspace.Services.GetRequiredService<IAnalyzerAssemblyLoaderProvider>();

		// Load all analyzers into a fresh shadow copied load context.  In the future, if we want to support reloading
		// of solution-level analyzer references, we should just need to listen for changes to those analyzer paths and
		// then call back into this method to update the solution accordingly.
		var analyzerLoader = loaderProvider.CreateNewShadowCopyLoader();

		foreach (var analyzerPath in solutionLevelAnalyzerPaths)
		{
			if (File.Exists(analyzerPath))
			{
				references.Add(new AnalyzerFileReference(analyzerPath, analyzerLoader));
				_logger.LogDebug($"Solution-level analyzer at {analyzerPath} added to workspace.");
			}
			else
			{
				_logger.LogWarning($"Solution-level analyzer at {analyzerPath} could not be found.");
			}
		}

		return references.ToImmutableAndClear();
	}
}
