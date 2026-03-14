using Microsoft.Extensions.Logging.Abstractions;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitServiceDiffTests
{
    private readonly GitService _gitService = new(new IdeOpenTabsFileManager(NullLogger<IdeOpenTabsFileManager>.Instance));

    [Fact]
    public async Task GetFileContentView_UntrackedFile_ProducesAddedRows()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("new-file.txt", """
            first
            second
            third
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);

        view.Kind.Should().Be(GitFileContentViewKind.Diff);
        view.DiffView.Should().NotBeNull();
        view.DiffView!.BaseLabel.Should().Be("Empty");
        view.DiffView.CanEditCurrent.Should().BeTrue();
        view.DiffView.Rows.Where(row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None).Should().OnlyContain(row => row.Kind == GitDiffDisplayRowKind.Added);
        view.UnstagedActions!.LineActions.Should().NotBeEmpty();
        view.StagedActions!.LineActions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCommitFileDiffView_RootCommit_UsesEmptyBase()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", """
            alpha
            bravo
            """);
        repo.Git("add sample.txt");
        var commitSha = repo.Commit("initial");

        var diffView = await _gitService.GetCommitFileDiffView(new GitCommitFileDiffRequest
        {
            RepoRootPath = repo.RootPath,
            CommitSha = commitSha,
            RepoRelativePath = "sample.txt"
        }, TestContext.Current.CancellationToken);

        diffView.Mode.Should().Be(GitDiffMode.Historical);
        diffView.BaseLabel.Should().Be("Empty");
        diffView.CanEditCurrent.Should().BeFalse();
        diffView.Rows.Should().Contain(row => row.Kind == GitDiffDisplayRowKind.Added && row.RightText == "alpha");
    }

    [Fact]
    public async Task GetCommitFileDiffView_MergeCommit_UsesFirstParent()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("common.txt", "base");
        repo.Git("add common.txt");
        repo.Commit("initial");

        repo.Git("checkout -b feature");
        repo.WriteFile("feature.txt", "feature branch");
        repo.Git("add feature.txt");
        var featureCommit = repo.Commit("feature change");

        repo.Git("checkout main");
        repo.WriteFile("main.txt", "main branch");
        repo.Git("add main.txt");
        repo.Commit("main change");
        repo.Git("merge --no-ff feature -m \"merge feature\"");
        var mergeCommit = repo.Git("rev-parse HEAD").Trim();

        var diffView = await _gitService.GetCommitFileDiffView(new GitCommitFileDiffRequest
        {
            RepoRootPath = repo.RootPath,
            CommitSha = mergeCommit,
            RepoRelativePath = "feature.txt"
        }, TestContext.Current.CancellationToken);

        diffView.Mode.Should().Be(GitDiffMode.Historical);
        diffView.Rows.Should().Contain(row => row.Kind == GitDiffDisplayRowKind.Added && row.RightText == "feature branch");
        mergeCommit.Should().NotBe(featureCommit);
    }

    [Fact]
    public async Task GetFileContentView_TrackedFileWithStagedAndUnstagedChanges_UsesCanonicalDiffAndAnchoredActions()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha staged
            bravo
            charlie unstaged
            delta
            """);
        repo.Git("add sample.txt");
        repo.WriteFile("sample.txt", """
            alpha staged
            bravo
            charlie unstaged again
            delta
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);

        view.Kind.Should().Be(GitFileContentViewKind.Diff);
        view.DiffView.Should().NotBeNull();
        view.DiffView!.BaseLabel.Should().Be("HEAD");
        view.DiffView.CurrentLabel.Should().Be("Current version");
        view.DiffView.Mode.Should().Be(GitDiffMode.Historical);
        view.DiffView.BaseDisplayText.Should().Contain("alpha");
        view.DiffView.BaseDisplayText.Should().NotContain("alpha staged");
        view.DiffView.CurrentDisplayText.Should().Contain("charlie unstaged again");
        view.UnstagedActions!.LineActions.Should().NotBeEmpty();
        view.StagedActions!.LineActions.Should().NotBeEmpty();
        view.RowStatesByRowId.Values.Should().Contain(state => state.StageState == GitDiffRowStageState.Staged);
        view.RowStatesByRowId.Values.Should().Contain(state =>
            state.StageState == GitDiffRowStageState.Unstaged ||
            state.StageState == GitDiffRowStageState.Mixed);
    }

    [Fact]
    public async Task GetFileContentView_NewlyAddedFileWithStagedAndUnstagedChanges_PreservesTrackedStageActions()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("new-file.txt", """
            alpha
            bravo
            """);
        repo.Git("add new-file.txt");
        repo.WriteFile("new-file.txt", """
            alpha
            bravo
            charlie
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);

        view.DiffView.Should().NotBeNull();
        view.DiffView!.Mode.Should().Be(GitDiffMode.Historical);
        view.UnstagedActions!.LineActions.Should().NotBeEmpty();
        view.StagedActions!.LineActions.Should().NotBeEmpty();
        view.RowStatesByRowId.Values.Should().Contain(state => state.StageState == GitDiffRowStageState.Staged);
        view.RowStatesByRowId.Values.Should().Contain(state =>
            state.StageState == GitDiffRowStageState.Unstaged ||
            state.StageState == GitDiffRowStageState.Mixed);
    }

    [Fact]
    public async Task GetFileContentView_StageUnstageDoesNotChangeCanonicalDisplayText()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha staged
            bravo
            charlie unstaged
            delta
            """);

        var beforeStage = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var stageLineId = beforeStage.UnstagedActions!.LineActions.Single(action => beforeStage.DiffView!.Rows.Any(row =>
            string.Equals(row.RowId, action.CanonicalRowId, StringComparison.Ordinal) &&
            string.Equals(row.RightText, "alpha staged", StringComparison.Ordinal))).LineActionId;

        await _gitService.StageLines(filePath, [stageLineId], TestContext.Current.CancellationToken);

        var afterStage = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);

        afterStage.DiffView!.BaseDisplayText.Should().Be(beforeStage.DiffView!.BaseDisplayText);
        afterStage.DiffView.CurrentDisplayText.Should().Be(beforeStage.DiffView.CurrentDisplayText);
        afterStage.DiffView.Rows.Select(static row => row.RowId).Should().Equal(beforeStage.DiffView.Rows.Select(static row => row.RowId));
    }

    [Fact]
    public async Task GetFileContentView_RepeatedBraceContext_KeepsInsertionsAnchoredToLocalBlock()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            {
                var isContiguous = marker.AnchorLine <= currentGroupEnd + 1;
                if (marker.Kind == current.Kind && isContiguous)
                {
                    currentGroupEnd = Math.Max(currentGroupEnd, marker.AnchorLine);
                    currentGroupCount += marker.LineSpan;
                    continue;
                }

                groupedMarkers.Add(new GitDiffScrollMarker
                {
                    Kind = current.Kind,
                    AnchorLine = currentGroupStart,
                    TargetLine = current.TargetLine,
                    LineSpan = Math.Max(currentGroupEnd - currentGroupStart + 1, currentGroupCount)
                });

                current = marker;
                currentGroupStart = marker.AnchorLine;
                currentGroupEnd = marker.AnchorLine;
                currentGroupCount = marker.LineSpan;
            }

            groupedMarkers.Add(new GitDiffScrollMarker
            {
                Kind = current.Kind,
                AnchorLine = currentGroupStart,
                TargetLine = current.TargetLine,
                LineSpan = Math.Max(currentGroupEnd - currentGroupStart + 1, currentGroupCount)
            });
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            {
                var isContiguous = marker.AnchorLine <= currentGroupEnd + 1;
                if (marker.Kind == current.Kind &&
                    marker.StageState == current.StageState &&
                    isContiguous)
                {
                    currentGroupEnd = Math.Max(currentGroupEnd, marker.AnchorLine);
                    currentGroupCount += marker.LineSpan;
                    continue;
                }

                groupedMarkers.Add(new GitDiffScrollMarker
                {
                    Kind = current.Kind,
                    StageState = current.StageState,
                    AnchorLine = currentGroupStart,
                    TargetLine = current.TargetLine,
                    LineSpan = Math.Max(currentGroupEnd - currentGroupStart + 1, currentGroupCount)
                });

                current = marker;
                currentGroupStart = marker.AnchorLine;
                currentGroupEnd = marker.AnchorLine;
                currentGroupCount = marker.LineSpan;
            }

            groupedMarkers.Add(new GitDiffScrollMarker
            {
                Kind = current.Kind,
                StageState = current.StageState,
                AnchorLine = currentGroupStart,
                TargetLine = current.TargetLine,
                LineSpan = Math.Max(currentGroupEnd - currentGroupStart + 1, currentGroupCount)
            });
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var rows = view.DiffView!.Rows;

        var conditionInsertionIndex = rows
            .Select((row, index) => (row, index))
            .Single(item => item.row.Kind == GitDiffDisplayRowKind.Added &&
                            item.row.RightText.Contains("marker.StageState == current.StageState", StringComparison.Ordinal))
            .index;
        rows[conditionInsertionIndex - 1].RightText.Trim().Should().Be("if (marker.Kind == current.Kind &&");
        rows[conditionInsertionIndex + 1].RightText.Trim().Should().Be("isContiguous)");

        var objectInitializerInsertions = rows
            .Select((row, index) => (row, index))
            .Where(item => item.row.Kind == GitDiffDisplayRowKind.Added &&
                           string.Equals(item.row.RightText.Trim(), "StageState = current.StageState,", StringComparison.Ordinal))
            .ToArray();
        objectInitializerInsertions.Should().HaveCount(2);
        foreach (var insertion in objectInitializerInsertions)
        {
            rows[insertion.index - 1].RightText.Trim().Should().Be("Kind = current.Kind,");
            rows[insertion.index + 1].RightText.Trim().Should().Be("AnchorLine = currentGroupStart,");
        }
    }

    [Fact]
    public async Task StageLines_MultiLineHunk_StagesOnlySelectedLine()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha staged
            bravo staged
            charlie
            delta
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var lineActionId = GetLineActionId(
            view,
            view.UnstagedActions!,
            row => row.LeftFileLineNumber == 1 && string.Equals(row.RightText, "alpha staged", StringComparison.Ordinal));

        await _gitService.StageLines(filePath, [lineActionId], TestContext.Current.CancellationToken);

        repo.Git("diff --cached --unified=0 -- sample.txt").Should().Contain("+alpha staged").And.NotContain("+bravo staged");
        repo.Git("diff --unified=0 -- sample.txt").Should().Contain("+bravo staged").And.NotContain("+alpha staged");
    }

    [Fact]
    public async Task UnstageLines_MultiLineHunk_UnstagesOnlySelectedLine()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha staged
            bravo staged
            charlie
            delta
            """);
        repo.Git("add sample.txt");

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var lineActionId = GetLineActionId(
            view,
            view.StagedActions!,
            row => row.LeftFileLineNumber == 1 && string.Equals(row.RightText, "alpha staged", StringComparison.Ordinal));

        await _gitService.UnstageLines(filePath, [lineActionId], TestContext.Current.CancellationToken);

        repo.Git("diff --cached --unified=0 -- sample.txt").Should().Contain("+bravo staged").And.NotContain("+alpha staged");
        repo.Git("diff --unified=0 -- sample.txt").Should().Contain("+alpha staged").And.NotContain("+bravo staged");
    }

    [Fact]
    public async Task StageLines_SingleLineModification_StagesWithoutWholeHunkFallback()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha
            bravo updated
            charlie
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var lineActionId = GetLineActionId(
            view,
            view.UnstagedActions!,
            row => row.LeftFileLineNumber == 2 && string.Equals(row.RightText, "bravo updated", StringComparison.Ordinal));

        await _gitService.StageLines(filePath, [lineActionId], TestContext.Current.CancellationToken);

        repo.Git("diff --cached --unified=0 -- sample.txt").Should().Contain("bravo updated");
        repo.Git("diff --unified=0 -- sample.txt").Should().BeEmpty();
    }

    [Fact]
    public async Task StageLines_InsertionOnlyHunk_StagesOnlySelectedAddedLine()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            omega
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha
            first insert
            second insert
            omega
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var lineActionId = GetLineActionId(
            view,
            view.UnstagedActions!,
            row => row.RightFileLineNumber == 2 && string.Equals(row.RightText, "first insert", StringComparison.Ordinal));

        await _gitService.StageLines(filePath, [lineActionId], TestContext.Current.CancellationToken);

        repo.Git("diff --cached --unified=0 -- sample.txt").Should().Contain("+first insert").And.NotContain("+second insert");
        repo.Git("diff --unified=0 -- sample.txt").Should().Contain("+second insert").And.NotContain("+first insert");
    }

    [Fact]
    public async Task StageLines_RepeatedReplacementText_UsesDeterministicRowAnchoring()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            one
            two
            three
            four
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            dup
            dup
            three
            four
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var secondLineActionId = GetLineActionId(
            view,
            view.UnstagedActions!,
            row => row.LeftFileLineNumber == 2 &&
                   row.RightFileLineNumber == 2 &&
                   string.Equals(row.LeftText, "two", StringComparison.Ordinal) &&
                   string.Equals(row.RightText, "dup", StringComparison.Ordinal));

        await _gitService.StageLines(filePath, [secondLineActionId], TestContext.Current.CancellationToken);

        var cachedDiff = repo.Git("diff --cached --unified=0 -- sample.txt");
        cachedDiff.Should().Contain("-two").And.Contain("+dup").And.NotContain("-one");

        var unstagedDiff = repo.Git("diff --unified=0 -- sample.txt");
        unstagedDiff.Should().Contain("-one").And.Contain("+dup").And.NotContain("-two");
    }

    [Fact]
    public async Task GetFileContentView_ModifiedThenAddedWithinSingleGitHunk_SplitsCustomChunks()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated
            inserted
            bravo
            charlie
            delta
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var changedRows = view.DiffView!.Rows.Where(row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None).ToArray();

        changedRows.Should().HaveCount(2);
        changedRows[0].Kind.Should().Be(GitDiffDisplayRowKind.ModifiedRight);
        changedRows[1].Kind.Should().Be(GitDiffDisplayRowKind.Added);
        changedRows.Select(static row => row.ChunkId).Distinct().Should().HaveCount(2);
        view.DiffView.Chunks.Should().HaveCount(2);
        view.UnstagedActions!.ChunkActions.Select(static action => action.ActionChunkId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFileContentView_ModifiedThenRemovedWithinSingleGitHunk_SplitsCustomChunks()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated
            charlie
            delta
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var changedRows = view.DiffView!.Rows.Where(row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None).ToArray();

        changedRows.Should().HaveCount(2);
        changedRows[0].Kind.Should().Be(GitDiffDisplayRowKind.ModifiedRight);
        changedRows[1].Kind.Should().Be(GitDiffDisplayRowKind.Removed);
        changedRows.Select(static row => row.ChunkId).Distinct().Should().HaveCount(2);
        view.DiffView.Chunks.Should().HaveCount(2);
    }

    [Fact]
    public async Task StageChunk_CustomHunkStagesOnlySelectedSubrangeWithinSingleGitHunk()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated
            inserted
            bravo
            charlie
            delta
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var modifiedChunkId = GetChunkId(
            view,
            row => row.Kind == GitDiffDisplayRowKind.ModifiedRight &&
                   string.Equals(row.RightText, "alpha updated", StringComparison.Ordinal));

        await _gitService.StageChunk(filePath, modifiedChunkId, TestContext.Current.CancellationToken);

        repo.Git("diff --cached --unified=0 -- sample.txt").Should().Contain("+alpha updated").And.NotContain("+inserted");
        repo.Git("diff --unified=0 -- sample.txt").Should().Contain("+inserted").And.NotContain("+alpha updated");
    }

    [Fact]
    public async Task UnstageChunk_CustomHunkUnstagesOnlySelectedSubrangeWithinSingleGitHunk()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated
            inserted
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var insertedChunkId = GetChunkId(
            view,
            row => row.Kind == GitDiffDisplayRowKind.Added &&
                   string.Equals(row.RightText, "inserted", StringComparison.Ordinal));

        await _gitService.UnstageChunk(filePath, insertedChunkId, TestContext.Current.CancellationToken);

        repo.Git("diff --cached --unified=0 -- sample.txt").Should().Contain("+alpha updated").And.NotContain("+inserted");
        repo.Git("diff --unified=0 -- sample.txt").Should().Contain("+inserted").And.NotContain("+alpha updated");
    }

    [Fact]
    public async Task RevertChunk_CustomHunkRevertsOnlySelectedSubrangeWithinSingleGitHunk()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated
            inserted
            bravo
            charlie
            delta
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var insertedChunkId = GetChunkId(
            view,
            row => row.Kind == GitDiffDisplayRowKind.Added &&
                   string.Equals(row.RightText, "inserted", StringComparison.Ordinal));

        await _gitService.RevertChunk(filePath, insertedChunkId, TestContext.Current.CancellationToken);

        File.ReadAllText(filePath).Should().Be("""
            alpha updated
            bravo
            charlie
            delta
            """ + Environment.NewLine);
        repo.Git("diff --unified=0 -- sample.txt").Should().Contain("+alpha updated").And.NotContain("+inserted");
    }

    [Fact]
    public async Task GetFileContentView_PartialStageKeepsCustomChunkGeometryStable()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated
            inserted
            bravo
            charlie
            delta
            """);

        var beforeStage = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var modifiedChunkId = GetChunkId(
            beforeStage,
            row => row.Kind == GitDiffDisplayRowKind.ModifiedRight &&
                   string.Equals(row.RightText, "alpha updated", StringComparison.Ordinal));
        var beforeChunkIds = beforeStage.DiffView!.Chunks.Select(static chunk => chunk.ChunkId).ToArray();

        await _gitService.StageChunk(filePath, modifiedChunkId, TestContext.Current.CancellationToken);

        var afterStage = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        afterStage.DiffView!.Chunks.Select(static chunk => chunk.ChunkId).Should().Equal(beforeChunkIds);
    }

    [Fact]
    public async Task GetFileContentView_PartialStage_HighlightsOnlyStagedRowInsideModifiedChunk()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            delta
            echo
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha updated once
            bravo staged target
            charlie updated once
            delta
            echo
            """);
        var beforeStage = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var middleLineActionId = GetLineActionId(
            beforeStage,
            beforeStage.UnstagedActions!,
            row => row.LeftFileLineNumber == 2 &&
                   row.RightFileLineNumber == 2 &&
                   string.Equals(row.RightText, "bravo staged target", StringComparison.Ordinal));

        await _gitService.StageLines(filePath, [middleLineActionId], TestContext.Current.CancellationToken);

        var afterStage = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var changedRows = afterStage.DiffView!.Rows
            .Where(row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None)
            .ToArray();

        changedRows.Should().HaveCount(3);
        afterStage.RowStatesByRowId[changedRows[0].RowId].StageState.Should().Be(GitDiffRowStageState.Unstaged);
        afterStage.RowStatesByRowId[changedRows[1].RowId].StageState.Should().Be(GitDiffRowStageState.Staged);
        afterStage.RowStatesByRowId[changedRows[2].RowId].StageState.Should().Be(GitDiffRowStageState.Unstaged);
    }

    [Fact]
    public async Task GetFileContentView_SameVisibleRowWithStagedAndUnstagedEdits_IsMixed()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var filePath = repo.WriteFile("sample.txt", """
            alpha
            bravo
            charlie
            """);
        repo.Git("add sample.txt");
        repo.Commit("initial");

        repo.WriteFile("sample.txt", """
            alpha
            bravo staged
            charlie
            """);
        repo.Git("add sample.txt");
        repo.WriteFile("sample.txt", """
            alpha
            bravo staged and unstaged
            charlie
            """);

        var view = await _gitService.GetFileContentView(filePath, TestContext.Current.CancellationToken);
        var changedRow = view.DiffView!.Rows.Single(row =>
            row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None &&
            row.LeftFileLineNumber == 2 &&
            row.RightFileLineNumber == 2);

        view.RowStatesByRowId[changedRow.RowId].StageState.Should().Be(GitDiffRowStageState.Mixed);
    }

    private static string GetLineActionId(
        GitFileContentViewModel view,
        GitDiffActionModel actions,
        Func<GitDiffDisplayRow, bool> rowPredicate)
    {
        return actions.LineActions.Single(action => view.DiffView!.Rows.Any(row =>
            string.Equals(row.RowId, action.CanonicalRowId, StringComparison.Ordinal) &&
            rowPredicate(row))).LineActionId;
    }

    private static string GetChunkId(
        GitFileContentViewModel view,
        Func<GitDiffDisplayRow, bool> rowPredicate)
    {
        return view.DiffView!.Rows.Single(rowPredicate).ChunkId;
    }
}
