using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace SharpIDE.Application.Features.Analysis;

/// The file system changes that turn one Roslyn solution into another, e.g. the result of a code action or rename - by file path, as a file may not be in the IDE's file tree
/// (e.g. an .editorconfig above the solution folder, or a linked source file). Mirrors Roslyn's LSP CodeActionResolveHelper, which produces the equivalent LSP WorkspaceEdit.
/// Applied in order: text changes (to files at their old paths), then renames, removals and additions.
public sealed class SolutionFileChanges
{
	/// One per file - linked documents (e.g. one per TFM) share a file
	public List<FileTextChange> ChangedFiles { get; } = [];
	public List<FileRename> RenamedFiles { get; } = [];
	public List<string> RemovedFilePaths { get; } = [];
	public List<FileAddition> AddedFiles { get; } = [];

	public bool IsEmpty => ChangedFiles.Count is 0 && RenamedFiles.Count is 0 && RemovedFilePaths.Count is 0 && AddedFiles.Count is 0;
}

public sealed record FileTextChange(string FilePath, SourceText OldText, ImmutableArray<TextChange> Changes);
public sealed record FileRename(string OldFilePath, string NewFilePath);
public sealed record FileAddition(string FilePath, SourceText Text);

/// Where to put the caret once a code action is applied, e.g. at a generated member, or in a newly created file
public sealed record SolutionFileNavigation(string FilePath, LinePosition Position);

public sealed record CodeActionApplyChanges(SolutionFileChanges Changes, SolutionFileNavigation? Navigation);
