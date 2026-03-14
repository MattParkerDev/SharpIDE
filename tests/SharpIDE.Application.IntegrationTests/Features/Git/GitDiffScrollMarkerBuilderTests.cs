using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitDiffScrollMarkerBuilderTests
{
    [Fact]
    public void BuildCurrentDocumentMarkers_GroupsContiguousCurrentSideChanges()
    {
        var diffView = new GitDiffViewModel
        {
            AbsolutePath = "/tmp/sample.cs",
            RepoRelativePath = "sample.cs",
            Mode = GitDiffMode.Unstaged,
            BaseLabel = "Index",
            CurrentLabel = "Current version",
            BaseDisplayText = string.Empty,
            CurrentDisplayText = string.Empty,
            CanEditCurrent = true,
            CanStageLines = true,
            CanUnstageLines = false,
            CanStageChunks = true,
            CanUnstageChunks = false,
            CanRevertChunks = true,
            Chunks = [],
            Rows =
            [
                CreateRow(1, GitDiffDisplayRowKind.Context, leftLine: 1, rightLine: 1),
                CreateRow(2, GitDiffDisplayRowKind.ModifiedRight, leftLine: 2, rightLine: 2),
                CreateRow(3, GitDiffDisplayRowKind.ModifiedRight, leftLine: 3, rightLine: 3),
                CreateRow(4, GitDiffDisplayRowKind.Added, leftLine: null, rightLine: 4)
            ]
        };

        var markers = GitDiffScrollMarkerBuilder.BuildCurrentDocumentMarkers(diffView);

        markers.Should().HaveCount(2);
        markers[0].Kind.Should().Be(GitDiffScrollMarkerKind.Modified);
        markers[0].StageState.Should().Be(GitDiffRowStageState.None);
        markers[0].AnchorLine.Should().Be(1);
        markers[0].TargetLine.Should().Be(1);
        markers[0].LineSpan.Should().Be(2);
        markers[1].Kind.Should().Be(GitDiffScrollMarkerKind.Added);
        markers[1].StageState.Should().Be(GitDiffRowStageState.None);
        markers[1].AnchorLine.Should().Be(3);
    }

    [Fact]
    public void BuildCurrentDocumentMarkers_AnchorsRemovedRowsToNearestCurrentLine()
    {
        var diffView = new GitDiffViewModel
        {
            AbsolutePath = "/tmp/sample.cs",
            RepoRelativePath = "sample.cs",
            Mode = GitDiffMode.Unstaged,
            BaseLabel = "Index",
            CurrentLabel = "Current version",
            BaseDisplayText = string.Empty,
            CurrentDisplayText = string.Empty,
            CanEditCurrent = true,
            CanStageLines = true,
            CanUnstageLines = false,
            CanStageChunks = true,
            CanUnstageChunks = false,
            CanRevertChunks = true,
            Chunks = [],
            Rows =
            [
                CreateRow(1, GitDiffDisplayRowKind.Removed, leftLine: 1, rightLine: null),
                CreateRow(2, GitDiffDisplayRowKind.Removed, leftLine: 2, rightLine: null),
                CreateRow(3, GitDiffDisplayRowKind.Context, leftLine: 3, rightLine: 1),
                CreateRow(4, GitDiffDisplayRowKind.Removed, leftLine: 4, rightLine: null),
                CreateRow(5, GitDiffDisplayRowKind.Context, leftLine: 5, rightLine: 2)
            ]
        };

        var markers = GitDiffScrollMarkerBuilder.BuildCurrentDocumentMarkers(diffView);

        markers.Should().HaveCount(1);
        markers[0].Kind.Should().Be(GitDiffScrollMarkerKind.Removed);
        markers[0].StageState.Should().Be(GitDiffRowStageState.None);
        markers[0].AnchorLine.Should().Be(0);
        markers[0].TargetLine.Should().Be(0);
        markers[0].LineSpan.Should().Be(3);
    }

    [Fact]
    public void BuildBaseDocumentMarkers_GroupsModifiedAndRemovedRowsForBaseSide()
    {
        var diffView = new GitDiffViewModel
        {
            AbsolutePath = "/tmp/sample.cs",
            RepoRelativePath = "sample.cs",
            Mode = GitDiffMode.Unstaged,
            BaseLabel = "Index",
            CurrentLabel = "Current version",
            BaseDisplayText = string.Empty,
            CurrentDisplayText = string.Empty,
            CanEditCurrent = true,
            CanStageLines = true,
            CanUnstageLines = false,
            CanStageChunks = true,
            CanUnstageChunks = false,
            CanRevertChunks = true,
            Chunks = [],
            Rows =
            [
                CreateRow(1, GitDiffDisplayRowKind.ModifiedLeft, leftLine: 4, rightLine: 4),
                CreateRow(2, GitDiffDisplayRowKind.Removed, leftLine: 5, rightLine: null),
                CreateRow(3, GitDiffDisplayRowKind.Removed, leftLine: 6, rightLine: null),
                CreateRow(4, GitDiffDisplayRowKind.Added, leftLine: null, rightLine: 5)
            ]
        };

        var markers = GitDiffScrollMarkerBuilder.BuildBaseDocumentMarkers(diffView);

        markers.Should().HaveCount(2);
        markers[0].Kind.Should().Be(GitDiffScrollMarkerKind.Modified);
        markers[0].StageState.Should().Be(GitDiffRowStageState.None);
        markers[0].AnchorLine.Should().Be(3);
        markers[0].TargetLine.Should().Be(3);
        markers[0].LineSpan.Should().Be(1);
        markers[1].Kind.Should().Be(GitDiffScrollMarkerKind.Removed);
        markers[1].StageState.Should().Be(GitDiffRowStageState.None);
        markers[1].AnchorLine.Should().Be(4);
        markers[1].TargetLine.Should().Be(4);
        markers[1].LineSpan.Should().Be(2);
    }

    [Fact]
    public void BuildCurrentDocumentMarkers_AnchorsRemovedRowToNextCurrentGap()
    {
        var diffView = new GitDiffViewModel
        {
            AbsolutePath = "/tmp/sample.cs",
            RepoRelativePath = "sample.cs",
            Mode = GitDiffMode.Unstaged,
            BaseLabel = "Index",
            CurrentLabel = "Current version",
            BaseDisplayText = string.Empty,
            CurrentDisplayText = string.Empty,
            CanEditCurrent = true,
            CanStageLines = true,
            CanUnstageLines = false,
            CanStageChunks = true,
            CanUnstageChunks = false,
            CanRevertChunks = true,
            Chunks = [],
            Rows =
            [
                CreateRow(1, GitDiffDisplayRowKind.Context, leftLine: 10, rightLine: 10),
                CreateRow(2, GitDiffDisplayRowKind.Removed, leftLine: 11, rightLine: null),
                CreateRow(3, GitDiffDisplayRowKind.Context, leftLine: 12, rightLine: 11)
            ]
        };

        var markers = GitDiffScrollMarkerBuilder.BuildCurrentDocumentMarkers(diffView);

        markers.Should().HaveCount(1);
        markers[0].Kind.Should().Be(GitDiffScrollMarkerKind.Removed);
        markers[0].AnchorLine.Should().Be(10);
        markers[0].TargetLine.Should().Be(10);
        markers[0].LineSpan.Should().Be(1);
    }

    private static GitDiffDisplayRow CreateRow(int displayIndex, GitDiffDisplayRowKind kind, int? leftLine, int? rightLine)
    {
        return new GitDiffDisplayRow
        {
            RowId = $"row-{displayIndex}",
            DisplayIndex = displayIndex,
            ChunkId = "chunk-1",
            Kind = kind,
            LeftText = string.Empty,
            RightText = string.Empty,
            LeftFileLineNumber = leftLine,
            RightFileLineNumber = rightLine,
            IsSyntheticLeft = leftLine is null,
            IsSyntheticRight = rightLine is null,
            IsEditableRight = rightLine is not null,
            ChunkBackgroundKind = GitDiffChunkBackgroundKind.None,
            InlineHighlightsLeft = [],
            InlineHighlightsRight = []
        };
    }
}
