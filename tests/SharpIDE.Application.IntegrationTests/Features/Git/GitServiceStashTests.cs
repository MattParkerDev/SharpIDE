using Microsoft.Extensions.Logging.Abstractions;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitServiceStashTests
{
    private readonly GitService _gitService = new(new IdeOpenTabsFileManager(NullLogger<IdeOpenTabsFileManager>.Instance));

    [Fact]
    public async Task GetSnapshot_MapsStageDisplayStates()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("staged.txt", "base staged");
        repo.WriteFile("unstaged.txt", "base unstaged");
        repo.WriteFile("partial.txt", "base partial");
        repo.Git("add staged.txt unstaged.txt partial.txt");
        repo.Commit("initial");

        repo.WriteFile("staged.txt", "staged only");
        repo.Git("add staged.txt");

        repo.WriteFile("unstaged.txt", "unstaged only");

        repo.WriteFile("partial.txt", "partial staged");
        repo.Git("add partial.txt");
        repo.WriteFile("partial.txt", "partial staged and unstaged");

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);

        snapshot.WorkingTreeEntries.Single(entry => entry.RepoRelativePath == "staged.txt").StageDisplayState.Should().Be(GitStageDisplayState.Staged);
        snapshot.WorkingTreeEntries.Single(entry => entry.RepoRelativePath == "unstaged.txt").StageDisplayState.Should().Be(GitStageDisplayState.Unstaged);
        snapshot.WorkingTreeEntries.Single(entry => entry.RepoRelativePath == "partial.txt").StageDisplayState.Should().Be(GitStageDisplayState.Partial);
    }

    [Fact]
    public async Task StagePaths_CaseDistinctFiles_StagesEachPath()
    {
        if (!SupportsCaseDistinctPaths())
        {
            return;
        }

        using var repo = await TempGitRepo.CreateAsync();
        var upperPath = repo.WriteFile("Case.txt", "upper");
        var lowerPath = repo.WriteFile("case.txt", "lower");
        repo.Git("add Case.txt case.txt");
        repo.Commit("initial");

        repo.WriteFile("Case.txt", "upper modified");
        repo.WriteFile("case.txt", "lower modified");

        await _gitService.StagePaths(repo.RootPath, [upperPath, lowerPath], TestContext.Current.CancellationToken);

        var stagedPaths = repo.Git("diff --cached --name-only --")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        stagedPaths.Should().HaveCount(2);
        stagedPaths.Should().Contain("Case.txt");
        stagedPaths.Should().Contain("case.txt");
    }

    [Fact]
    public async Task GetSnapshot_LoadsTrackedAndUntrackedStashFiles()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", "tracked change");
        repo.WriteFile("untracked.txt", "untracked change");
        repo.Git("stash push -u -m \"panel stash\"");

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);

        snapshot.Stashes.Should().ContainSingle();
        snapshot.Stashes[0].StashRef.Should().Be("stash@{0}");
        snapshot.Stashes[0].Files.Should().Contain(file =>
            file.RepoRelativePath == "tracked.txt" &&
            file.ContentKind == GitStashFileContentKind.WorkingTreeSnapshot);
        snapshot.Stashes[0].Files.Should().Contain(file =>
            file.RepoRelativePath == "untracked.txt" &&
            file.ContentKind == GitStashFileContentKind.UntrackedSnapshot);
    }

    [Fact]
    public async Task GetSnapshot_LoadsMultipleStashes()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", "first stash");
        repo.Git("stash push -m \"first\"");
        repo.WriteFile("tracked.txt", "second stash");
        repo.Git("stash push -m \"second\"");

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);

        snapshot.Stashes.Should().HaveCount(2);
        snapshot.Stashes.Select(stash => stash.StashRef).Should().Contain(["stash@{0}", "stash@{1}"]);
    }

    [Fact]
    public async Task GetStashFileDiffView_TrackedFile_UsesWorkingTreeSnapshot()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("tracked.txt", """
            alpha
            bravo
            """);
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", """
            alpha changed
            bravo
            """);
        repo.Git("stash push -m \"tracked stash\"");

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);
        var stash = snapshot.Stashes.Single();
        var file = stash.Files.Single(candidate => candidate.RepoRelativePath == "tracked.txt");

        var diffView = await _gitService.GetStashFileDiffView(CreateRequest(repo.RootPath, stash, file), TestContext.Current.CancellationToken);

        diffView.Mode.Should().Be(GitDiffMode.Historical);
        diffView.CanEditCurrent.Should().BeFalse();
        diffView.Rows.Should().Contain(row => row.Kind == GitDiffDisplayRowKind.ModifiedRight && row.RightText == "alpha changed");
    }

    [Fact]
    public async Task GetStashFileDiffView_UntrackedFile_UsesThirdParentSnapshot()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("new-file.txt", """
            first
            second
            """);
        repo.Git("stash push -u -m \"untracked stash\"");

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);
        var stash = snapshot.Stashes.Single();
        var file = stash.Files.Single(candidate => candidate.RepoRelativePath == "new-file.txt");

        var diffView = await _gitService.GetStashFileDiffView(CreateRequest(repo.RootPath, stash, file), TestContext.Current.CancellationToken);

        diffView.BaseLabel.Should().Be("Empty");
        diffView.CanEditCurrent.Should().BeFalse();
        diffView.Rows.Should().Contain(row => row.Kind == GitDiffDisplayRowKind.Added && row.RightText == "first");
    }

    [Fact]
    public async Task StashCommands_ApplyPopAndDrop_WorkAgainstTempRepo()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var trackedPath = repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", "apply change");
        repo.Git("stash push -m \"apply stash\"");

        await _gitService.ApplyStash(repo.RootPath, "stash@{0}", TestContext.Current.CancellationToken);

        File.ReadAllText(trackedPath).Should().Contain("apply change");
        repo.Git("stash list").Should().Contain("apply stash");

        repo.Git("reset --hard HEAD");
        await _gitService.DropStash(repo.RootPath, "stash@{0}", TestContext.Current.CancellationToken);
        repo.Git("stash list").Should().BeEmpty();

        repo.WriteFile("tracked.txt", "pop change");
        repo.Git("stash push -m \"pop stash\"");

        await _gitService.PopStash(repo.RootPath, "stash@{0}", TestContext.Current.CancellationToken);

        File.ReadAllText(trackedPath).Should().Contain("pop change");
        repo.Git("stash list").Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyStashFileChanges_TrackedFile_RestoresThatFileOnly()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var trackedPath = repo.WriteFile("tracked.txt", "base");
        var otherPath = repo.WriteFile("other.txt", "other base");
        repo.Git("add tracked.txt other.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", "tracked stash version");
        repo.WriteFile("other.txt", "other stash version");
        repo.Git("stash push -m \"tracked file stash\"");
        repo.WriteFile("other.txt", "current other change");

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);
        var stash = snapshot.Stashes.Single();
        var file = stash.Files.Single(candidate => candidate.RepoRelativePath == "tracked.txt");

        await _gitService.ApplyStashFileChanges(CreateRequest(repo.RootPath, stash, file), TestContext.Current.CancellationToken);

        File.ReadAllText(trackedPath).Should().Contain("tracked stash version");
        File.ReadAllText(otherPath).Should().Contain("current other change");
    }

    [Fact]
    public async Task ApplyStashFileChanges_UntrackedFile_CreatesFileFromStashSnapshot()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        var untrackedPath = repo.WriteFile("new-file.txt", "stashed untracked");
        repo.Git("stash push -u -m \"untracked file stash\"");

        File.Exists(untrackedPath).Should().BeFalse();

        var snapshot = await _gitService.GetSnapshot(repo.RootPath, cancellationToken: TestContext.Current.CancellationToken);
        var stash = snapshot.Stashes.Single();
        var file = stash.Files.Single(candidate => candidate.RepoRelativePath == "new-file.txt");

        await _gitService.ApplyStashFileChanges(CreateRequest(repo.RootPath, stash, file), TestContext.Current.CancellationToken);

        File.ReadAllText(untrackedPath).Should().Contain("stashed untracked");
    }

    [Fact]
    public async Task DiscardPaths_TrackedAndUntrackedFiles_RestoreExpectedState()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var trackedPath = repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", "discard me");
        repo.Git("add tracked.txt");
        var untrackedPath = repo.WriteFile("untracked.txt", "temp");

        await _gitService.DiscardPaths(repo.RootPath, [trackedPath, untrackedPath], TestContext.Current.CancellationToken);

        File.ReadAllText(trackedPath).Should().Contain("base");
        File.Exists(untrackedPath).Should().BeFalse();
        repo.Git("status --short").Should().BeEmpty();
    }

    [Fact]
    public async Task DiscardUnstagedPaths_UnstagedTrackedFile_RestoresFromIndex()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var trackedPath = repo.WriteFile("tracked.txt", "base");
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", "unstaged change");

        await _gitService.DiscardUnstagedPaths(repo.RootPath, [trackedPath], TestContext.Current.CancellationToken);

        File.ReadAllText(trackedPath).Should().Contain("base");
        repo.Git("status --short").Should().BeEmpty();
    }

    [Fact]
    public async Task DiscardUnstagedPaths_PartialFile_KeepsStagedChangesAndRemovesWorktreeChanges()
    {
        using var repo = await TempGitRepo.CreateAsync();
        var trackedPath = repo.WriteFile("tracked.txt", """
            alpha
            bravo
            """);
        repo.Git("add tracked.txt");
        repo.Commit("initial");

        repo.WriteFile("tracked.txt", """
            alpha staged
            bravo
            """);
        repo.Git("add tracked.txt");
        repo.WriteFile("tracked.txt", """
            alpha staged
            bravo unstaged
            """);

        await _gitService.DiscardUnstagedPaths(repo.RootPath, [trackedPath], TestContext.Current.CancellationToken);

        File.ReadAllText(trackedPath).Should().Contain("alpha staged");
        File.ReadAllText(trackedPath).Should().NotContain("bravo unstaged");
        repo.Git("status --short").Trim().Should().Be("M  tracked.txt");
    }

    private static GitStashFileDiffRequest CreateRequest(string repoRoot, GitStashEntry stash, GitStashChangedFile file)
    {
        return new GitStashFileDiffRequest
        {
            RepoRootPath = repoRoot,
            StashRef = stash.StashRef,
            RepoRelativePath = file.RepoRelativePath,
            OldRepoRelativePath = file.OldRepoRelativePath,
            StatusCode = file.StatusCode,
            ContentKind = file.ContentKind
        };
    }

    private static bool SupportsCaseDistinctPaths()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"sharpide-case-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            File.WriteAllText(Path.Combine(rootPath, "Case.txt"), "upper");
            File.WriteAllText(Path.Combine(rootPath, "case.txt"), "lower");
            return Directory.GetFiles(rootPath).Length == 2;
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
