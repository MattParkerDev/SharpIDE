using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Git;

public enum GitWorkingTreeGroup
{
    ChangedFiles,
    UnversionedFiles
}

public enum GitStageDisplayState
{
    Unstaged,
    Staged,
    Partial
}

[Flags]
public enum GitWorkingTreeStatus
{
    None = 0,
    Modified = 1 << 0,
    Deleted = 1 << 1,
    Renamed = 1 << 2,
    TypeChange = 1 << 3,
    Conflicted = 1 << 4,
    Unversioned = 1 << 5
}

public sealed class GitRepositoryContext
{
    public required bool IsRepositoryDiscovered { get; init; }
    public required string RepoRootPath { get; init; }
    public required string GitDirectoryPath { get; init; }
    public required string BranchDisplayName { get; init; }
    public required bool IsDetachedHead { get; init; }
}

public sealed class GitWorkingTreeEntry
{
    public required string AbsolutePath { get; init; }
    public required string RepoRelativePath { get; init; }
    public required GitWorkingTreeGroup Group { get; init; }
    public required GitWorkingTreeStatus Status { get; init; }
    public required GitStageDisplayState StageDisplayState { get; init; }
    public required bool IsStaged { get; init; }
    public required bool IsTracked { get; init; }
    public string FileName => Path.GetFileName(AbsolutePath);
    public string DirectoryDisplayPath => Path.GetDirectoryName(RepoRelativePath) ?? string.Empty;
}

public sealed class GitCommitSummary
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Subject { get; init; }
    public required string AuthorName { get; init; }
    public required DateTimeOffset AuthoredAt { get; init; }
    public required bool IsMergeCommit { get; init; }
}

public enum GitRefKind
{
    Head,
    Category,
    LocalBranch,
    RemoteBranch,
    Tag
}

public sealed class GitRefNode
{
    public required string DisplayName { get; init; }
    public string? RefName { get; init; }
    public required GitRefKind Kind { get; init; }
    public required bool IsSelectable { get; init; }
    public required bool IsCurrent { get; init; }
    public required bool IsMain { get; init; }
    public required IReadOnlyList<GitRefNode> Children { get; init; }
}

public enum GitHistorySearchMode
{
    None,
    CommitMetadata,
    Paths
}

public sealed class GitHistoryQuery
{
    public bool IncludeAllRefs { get; init; }
    public string? RefName { get; init; }
    public string SearchTerm { get; init; } = string.Empty;
    public required GitHistorySearchMode SearchMode { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}

public enum GitGraphCellKind
{
    Commit,
    Vertical,
    Horizontal,
    SlashUp,
    SlashDown
}

public sealed class GitGraphCell
{
    public required GitGraphCellKind Kind { get; init; }
    public required int ColumnIndex { get; init; }
    public required int ColorIndex { get; init; }
}

public enum GitGraphAnchor
{
    Top,
    Center,
    Bottom
}

public sealed class GitGraphSegment
{
    public required int FromColumnIndex { get; init; }
    public required GitGraphAnchor FromAnchor { get; init; }
    public required int ToColumnIndex { get; init; }
    public required GitGraphAnchor ToAnchor { get; init; }
    public required int ColorIndex { get; init; }
}

public sealed class GitHistoryRow
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Subject { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthoredAt { get; init; }
    public required DateTimeOffset CommittedAt { get; init; }
    public required string FriendlyTimestamp { get; init; }
    public required string FriendlyCommittedTimestamp { get; init; }
    public required bool IsMergeCommit { get; init; }
    public required bool IsLocalAuthor { get; init; }
    public required bool IsSelectable { get; init; }
    public required bool IsPrimaryBranchCommit { get; init; }
    public required int CommitLaneIndex { get; init; }
    public required int CommitColorIndex { get; init; }
    public required IReadOnlyList<GitGraphSegment> GraphSegments { get; init; }
    public required string GraphPrefix { get; init; }
    public required IReadOnlyList<GitGraphCell> GraphCells { get; init; }
    public required IReadOnlyList<string> Decorations { get; init; }
}

public sealed class GitHistoryPage
{
    public required IReadOnlyList<GitHistoryRow> Rows { get; init; }
    public required bool HasMore { get; init; }
}

public sealed class GitCommitDetails
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string FullMessage { get; init; }
    public required string Subject { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthoredAt { get; init; }
    public required DateTimeOffset CommittedAt { get; init; }
    public required string FriendlyTimestamp { get; init; }
    public required string FriendlyCommittedTimestamp { get; init; }
    public required IReadOnlyList<string> ParentShas { get; init; }
}

public sealed class GitCommitChangedFile
{
    public required string RepoRelativePath { get; init; }
    public string? OldRepoRelativePath { get; init; }
    public required string StatusCode { get; init; }
    public required string DisplayPath { get; init; }
}

public sealed class GitStashEntry
{
    public required string StashRef { get; init; }
    public required string Message { get; init; }
    public required IReadOnlyList<GitStashChangedFile> Files { get; init; }
}

public sealed class GitStashChangedFile
{
    public required string RepoRelativePath { get; init; }
    public string? OldRepoRelativePath { get; init; }
    public required string StatusCode { get; init; }
    public required string DisplayPath { get; init; }
    public required GitStashFileContentKind ContentKind { get; init; }
}

public enum GitStashFileContentKind
{
    WorkingTreeSnapshot,
    UntrackedSnapshot
}

public sealed class GitCommitFileDiffRequest
{
    public required string RepoRootPath { get; init; }
    public required string CommitSha { get; init; }
    public required string RepoRelativePath { get; init; }
    public string? OldRepoRelativePath { get; init; }
}

public sealed class GitStashFileDiffRequest
{
    public required string RepoRootPath { get; init; }
    public required string StashRef { get; init; }
    public required string RepoRelativePath { get; init; }
    public string? OldRepoRelativePath { get; init; }
    public required string StatusCode { get; init; }
    public required GitStashFileContentKind ContentKind { get; init; }
}

public enum GitFileContentViewKind
{
    Empty,
    Diff,
    MergeConflict
}

public enum GitDiffMode
{
    Unstaged,
    Staged,
    Untracked,
    Historical
}

public enum GitDiffDisplayRowKind
{
    Context,
    Added,
    Removed,
    ModifiedLeft,
    ModifiedRight,
    Spacer
}

public enum GitDiffChunkBackgroundKind
{
    None,
    Modified,
    Added,
    Removed
}

public enum GitInlineHighlightKind
{
    Added,
    Removed,
    Modified
}

public enum GitPatchSelectionType
{
    Chunk,
    Line
}

public enum GitPatchOperationMode
{
    Stage,
    Unstage,
    Revert
}

public enum GitDiffRowStageState
{
    None,
    Staged,
    Unstaged,
    Mixed
}

public sealed class GitInlineHighlightSpan
{
    public required int StartColumn { get; init; }
    public required int Length { get; init; }
    public required GitInlineHighlightKind HighlightKind { get; init; }
}

public sealed class GitDiffDisplayRow
{
    public required string RowId { get; init; }
    public required int DisplayIndex { get; init; }
    public required string ChunkId { get; init; }
    public required GitDiffDisplayRowKind Kind { get; init; }
    public required string LeftText { get; init; }
    public required string RightText { get; init; }
    public int? LeftFileLineNumber { get; init; }
    public int? RightFileLineNumber { get; init; }
    public required bool IsSyntheticLeft { get; init; }
    public required bool IsSyntheticRight { get; init; }
    public required bool IsEditableRight { get; init; }
    public required GitDiffChunkBackgroundKind ChunkBackgroundKind { get; init; }
    public required IReadOnlyList<GitInlineHighlightSpan> InlineHighlightsLeft { get; init; }
    public required IReadOnlyList<GitInlineHighlightSpan> InlineHighlightsRight { get; init; }
}

public sealed class GitDiffChunk
{
    public required string ChunkId { get; init; }
    public required int FirstDisplayRow { get; init; }
    public required int LastDisplayRow { get; init; }
    public required string FirstRowId { get; init; }
    public required string LastRowId { get; init; }
    public required GitDiffChunkBackgroundKind BackgroundKind { get; init; }
}

public sealed class GitPatchSelection
{
    public required GitPatchSelectionType SelectionType { get; init; }
    public string? ChunkId { get; init; }
    public required IReadOnlyList<string> LineActionIds { get; init; }
}

public sealed class GitDiffViewModel
{
    public required string AbsolutePath { get; init; }
    public required string RepoRelativePath { get; init; }
    public required GitDiffMode Mode { get; init; }
    public required string BaseLabel { get; init; }
    public required string CurrentLabel { get; init; }
    public required string BaseDisplayText { get; init; }
    public required string CurrentDisplayText { get; init; }
    public required bool CanEditCurrent { get; init; }
    public required bool CanStageLines { get; init; }
    public required bool CanUnstageLines { get; init; }
    public required bool CanStageChunks { get; init; }
    public required bool CanUnstageChunks { get; init; }
    public required bool CanRevertChunks { get; init; }
    public required IReadOnlyList<GitDiffDisplayRow> Rows { get; init; }
    public required IReadOnlyList<GitDiffChunk> Chunks { get; init; }
}

public sealed class GitDiffRowState
{
    public required string RowId { get; init; }
    public required GitDiffRowStageState StageState { get; init; }
}

public sealed class GitDiffLineActionAnchor
{
    public required string LineActionId { get; init; }
    public required string PatchText { get; init; }
    public required GitPatchOperationMode OperationMode { get; init; }
    public required string CanonicalRowId { get; init; }
}

public sealed class GitDiffChunkActionAnchor
{
    public required string ActionChunkId { get; init; }
    public required string PatchText { get; init; }
    public required GitPatchOperationMode OperationMode { get; init; }
    public required string FirstCanonicalRowId { get; init; }
    public required string LastCanonicalRowId { get; init; }
    public required string AnchorCanonicalRowId { get; init; }
}

public sealed class GitDiffActionModel
{
    public required IReadOnlyList<GitDiffLineActionAnchor> LineActions { get; init; }
    public required IReadOnlyList<GitDiffChunkActionAnchor> ChunkActions { get; init; }
}

public sealed class GitMergeConflictViewModel
{
    public required string AbsolutePath { get; init; }
    public required string RepoRelativePath { get; init; }
    public required string LocalText { get; init; }
    public required string CurrentText { get; init; }
    public required string IncomingText { get; init; }
}

public sealed class GitFileContentViewModel
{
    public required GitFileContentViewKind Kind { get; init; }
    public required string AbsolutePath { get; init; }
    public required string RepoRelativePath { get; init; }
    public GitDiffViewModel? DiffView { get; init; }
    public GitDiffActionModel? UnstagedActions { get; init; }
    public GitDiffActionModel? StagedActions { get; init; }
    public IReadOnlyDictionary<string, GitDiffRowState> RowStatesByRowId { get; init; } = new Dictionary<string, GitDiffRowState>(StringComparer.Ordinal);
    public GitMergeConflictViewModel? MergeConflictView { get; init; }
}

public sealed class GitSnapshot
{
    public required GitRepositoryContext Repository { get; init; }
    public required IReadOnlyList<GitWorkingTreeEntry> WorkingTreeEntries { get; init; }
    public required IReadOnlyList<GitCommitSummary> RecentCommits { get; init; }
    public required IReadOnlyList<GitStashEntry> Stashes { get; init; }

    public int StagedEntryCount => WorkingTreeEntries.Count(entry => entry.IsStaged);

    public static GitSnapshot Empty() => new()
    {
        Repository = new GitRepositoryContext
        {
            IsRepositoryDiscovered = false,
            RepoRootPath = string.Empty,
            GitDirectoryPath = string.Empty,
            BranchDisplayName = "No repository",
            IsDetachedHead = false
        },
        WorkingTreeEntries = [],
        RecentCommits = [],
        Stashes = []
    };
}

public static class GitStatusMapper
{
    public static GitFileStatus ToSharpIdeFileStatus(GitWorkingTreeEntry entry)
    {
        return entry.Group switch
        {
            GitWorkingTreeGroup.UnversionedFiles => GitFileStatus.Added,
            _ => GitFileStatus.Modified
        };
    }
}
