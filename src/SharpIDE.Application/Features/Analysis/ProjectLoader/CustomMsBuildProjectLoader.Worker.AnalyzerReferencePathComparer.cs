using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpIDE.Application.Features.Analysis.ProjectLoader;

public partial class CustomMsBuildProjectLoader
{
	private sealed partial class Worker
	{
		private sealed class AnalyzerReferencePathComparer : IEqualityComparer<AnalyzerReference?>
		{
			public static AnalyzerReferencePathComparer Instance = new();

			private AnalyzerReferencePathComparer() { }

			public bool Equals(AnalyzerReference? x, AnalyzerReference? y)
				=> string.Equals(x?.FullPath, y?.FullPath, StringComparison.OrdinalIgnoreCase);

			public int GetHashCode(AnalyzerReference? obj)
				=> obj?.FullPath?.GetHashCode() ?? 0;
		}
	}
}
