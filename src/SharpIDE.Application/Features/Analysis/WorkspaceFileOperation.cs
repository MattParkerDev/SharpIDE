using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Analysis;

/// A file added to, removed from, or moved/renamed within the solution folder, to be reflected in the Roslyn workspace.
/// Applied in batches via RoslynAnalysis.ApplyFileOperations, so e.g. a branch switch is one workspace change.
public closed record WorkspaceFileOperation
{
	/// Added to the project whose globs include it, if any
	public sealed record Add(SharpIdeFile File, string Content) : WorkspaceFileOperation;

	/// Removes every document with this path (e.g. one per target framework)
	public sealed record Remove(string FilePath) : WorkspaceFileOperation;

	/// File already has its new path and name. Kept as the same document if it stays in the same project, so it isn't reparsed.
	public sealed record Move(SharpIdeFile File, string OldFilePath) : WorkspaceFileOperation;
}
