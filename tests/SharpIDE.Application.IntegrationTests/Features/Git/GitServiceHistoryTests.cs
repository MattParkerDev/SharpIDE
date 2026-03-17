using Microsoft.Extensions.Logging.Abstractions;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitServiceHistoryTests
{
    private readonly GitService _gitService = new(new IdeOpenTabsFileManager(NullLogger<IdeOpenTabsFileManager>.Instance));

    [Fact]
    public async Task GetRepositoryRefs_ReturnsCurrentAndMainBranchMarkers()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git("checkout -b feature/demo");
        repo.WriteFile("sample.txt", "beta");
        repo.Git("add sample.txt");
        repo.Commit("feature commit");

        var refs = await _gitService.GetRepositoryRefs(repo.RootPath, TestContext.Current.CancellationToken);

        refs.Should().ContainSingle(node => node.Kind == GitRefKind.Head && node.IsCurrent);
        var localNode = refs.Single(node => node.DisplayName == "Local");
        localNode.Children.Should().Contain(node => node.RefName == "refs/heads/main" && node.IsMain);
        localNode.Children.Should().Contain(node => node.RefName == "refs/heads/feature/demo" && node.IsCurrent);
    }

    [Fact]
    public async Task GetHistoryPage_PlainSearch_FindsBySubjectAndHash()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("app.txt", "one");
        repo.Git("add app.txt");
        repo.Commit("initial");
        repo.WriteFile("app.txt", "two");
        repo.Git("add app.txt");
        var targetCommit = repo.Commit("add parser support");

        var subjectResults = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = false,
            RefName = "refs/heads/main",
            SearchMode = GitHistorySearchMode.CommitMetadata,
            SearchTerm = "parser",
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        subjectResults.Rows.Should().ContainSingle(row => row.Sha == targetCommit);

        var hashResults = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = false,
            RefName = "refs/heads/main",
            SearchMode = GitHistorySearchMode.CommitMetadata,
            SearchTerm = targetCommit[..8],
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        hashResults.Rows.Should().ContainSingle(row => row.Sha == targetCommit);
    }

    [Fact]
    public async Task GetHistoryPage_PathSearch_FindsCommitsByTouchedFile()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("src/one.cs", "one");
        repo.Git("add src/one.cs");
        repo.Commit("initial");
        repo.WriteFile("src/two.cs", "two");
        repo.Git("add src/two.cs");
        var targetCommit = repo.Commit("add second file");

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = false,
            RefName = "refs/heads/main",
            SearchMode = GitHistorySearchMode.Paths,
            SearchTerm = "two.cs",
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        results.Rows.Should().ContainSingle(row => row.Sha == targetCommit);
    }

    [Fact]
    public async Task GetHistoryPage_BranchAndMerge_ProducesGraphCells()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");

        repo.Git("checkout -b feature");
        repo.WriteFile("feature.txt", "branch");
        repo.Git("add feature.txt");
        repo.Commit("feature work");

        repo.Git("checkout main");
        repo.WriteFile("main.txt", "main");
        repo.Git("add main.txt");
        repo.Commit("main work");
        repo.Git("merge --no-ff feature -m \"merge feature\"");

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = false,
            RefName = "refs/heads/main",
            SearchMode = GitHistorySearchMode.None,
            SearchTerm = string.Empty,
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        results.Rows.Should().Contain(row => row.IsMergeCommit);
        results.Rows.Should().Contain(row => row.GraphSegments.Any(segment => segment.FromColumnIndex != segment.ToColumnIndex));
    }

    [Fact]
    public async Task GetHistoryPage_AllRefs_IncludesCommitsFromNonCurrentBranches()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");

        repo.Git("checkout -b feature/demo");
        repo.WriteFile("feature.txt", "feature");
        repo.Git("add feature.txt");
        var featureCommit = repo.Commit("feature work");

        repo.Git("checkout main");
        repo.WriteFile("main.txt", "main");
        repo.Git("add main.txt");
        var mainCommit = repo.Commit("main work");

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = true,
            RefName = null,
            SearchMode = GitHistorySearchMode.None,
            SearchTerm = string.Empty,
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        results.Rows.Select(row => row.Sha).Should().Contain(featureCommit);
        results.Rows.Select(row => row.Sha).Should().Contain(mainCommit);
    }

    [Fact]
    public async Task GetHistoryPage_AllRefs_UsesCommitterDateOrdering()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial", committedAt: new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));

        repo.Git("checkout -b feature/date-order");
        repo.WriteFile("feature.txt", "feature");
        repo.Git("add feature.txt");
        var featureCommit = repo.Commit(
            "feature work",
            authoredAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            committedAt: new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero));

        repo.Git("checkout main");
        repo.WriteFile("main.txt", "main");
        repo.Git("add main.txt");
        var mainCommit = repo.Commit(
            "main work",
            authoredAt: new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero),
            committedAt: new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero));

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = true,
            RefName = null,
            SearchMode = GitHistorySearchMode.None,
            SearchTerm = string.Empty,
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        var featureRow = results.Rows.Single(row => row.Sha == featureCommit);
        var mainRow = results.Rows.Single(row => row.Sha == mainCommit);
        var orderedRows = results.Rows.ToList();

        orderedRows.IndexOf(featureRow).Should().BeLessThan(orderedRows.IndexOf(mainRow));
        featureRow.CommittedAt.Should().BeAfter(mainRow.CommittedAt);
        featureRow.AuthoredAt.Should().BeBefore(mainRow.AuthoredAt);
    }

    [Fact]
    public async Task GetHistoryPage_AllRefs_FlagsConfiguredLocalAuthor()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        var localCommit = repo.Commit("initial");

        repo.Git("checkout -b feature/external");
        repo.WriteFile("external.txt", "external");
        repo.Git("add external.txt");
        var externalCommit = repo.Commit("external work", authorName: "other-user", authorEmail: "other@example.com");

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = true,
            RefName = null,
            SearchMode = GitHistorySearchMode.None,
            SearchTerm = string.Empty,
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        results.Rows.Single(row => row.Sha == localCommit).IsLocalAuthor.Should().BeTrue();
        results.Rows.Single(row => row.Sha == externalCommit).IsLocalAuthor.Should().BeFalse();
    }

    [Fact]
    public async Task GetHistoryPage_AllRefs_PreservesGraphCellsForMergedAndUnmergedBranches()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");

        repo.WriteFile("second.txt", "second");
        repo.Git("add second.txt");
        var branchPoint = repo.Commit("second");

        repo.Git("checkout -b feature/merge");
        repo.WriteFile("feature.txt", "feature");
        repo.Git("add feature.txt");
        var featureCommit = repo.Commit("feature work");

        repo.Git("checkout main");
        repo.WriteFile("main.txt", "main");
        repo.Git("add main.txt");
        repo.Commit("main work");
        var mergeCommit = repo.Git("merge --no-ff feature/merge -m \"merge feature\"").Trim();
        mergeCommit = repo.Git("rev-parse HEAD").Trim();

        repo.Git($"checkout -b feature/left {branchPoint}");
        repo.WriteFile("left.txt", "left");
        repo.Git("add left.txt");
        var leftCommit = repo.Commit("left edge work");

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = true,
            RefName = null,
            SearchMode = GitHistorySearchMode.None,
            SearchTerm = string.Empty,
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        results.Rows.Should().Contain(row => row.Sha == featureCommit);
        results.Rows.Should().Contain(row => row.Sha == leftCommit);
        results.Rows.Should().Contain(row => row.Sha == mergeCommit && row.IsMergeCommit);
        results.Rows.Should().Contain(row => row.GraphSegments.Any(segment => segment.FromColumnIndex != segment.ToColumnIndex));
    }

    [Fact]
    public async Task GetHistoryPage_AllRefs_ExcludesTagOnlyDetachedCommits()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");

        repo.Git("checkout --detach");
        repo.WriteFile("tagged.txt", "tagged");
        repo.Git("add tagged.txt");
        var detachedCommit = repo.Commit("tag-only commit");
        repo.Git("tag detached-only");

        repo.Git("checkout main");

        var results = await _gitService.GetHistoryPage(repo.RootPath, new GitHistoryQuery
        {
            IncludeAllRefs = true,
            RefName = null,
            SearchMode = GitHistorySearchMode.None,
            SearchTerm = string.Empty,
            Skip = 0,
            Take = 50
        }, TestContext.Current.CancellationToken);

        results.Rows.Should().NotContain(row => row.Sha == detachedCommit);
    }

    [Fact]
    public async Task GetCommitDetails_ReturnsFullMessageAndAuthor()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("notes.txt", "hello");
        repo.Git("add notes.txt");
        repo.Git("commit -m \"subject\" -m \"body line\"");
        var commitSha = repo.Git("rev-parse HEAD").Trim();

        var details = await _gitService.GetCommitDetails(repo.RootPath, commitSha, TestContext.Current.CancellationToken);

        details.Sha.Should().Be(commitSha);
        details.AuthorName.Should().Be("sharpide-tests");
        details.AuthorEmail.Should().Be("test@example.com");
        details.FullMessage.Should().Contain("subject");
        details.FullMessage.Should().Contain("body line");
    }

    [Fact]
    public async Task GetCommitChangedFiles_Rename_ReturnsOldAndNewPath()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("old-name.txt", "hello");
        repo.Git("add old-name.txt");
        repo.Commit("initial");
        repo.Git("mv old-name.txt new-name.txt");
        repo.Git("commit -m \"rename file\"");
        var commitSha = repo.Git("rev-parse HEAD").Trim();

        var files = await _gitService.GetCommitChangedFiles(repo.RootPath, commitSha, TestContext.Current.CancellationToken);

        files.Should().ContainSingle();
        files[0].StatusCode.Should().Be("R");
        files[0].OldRepoRelativePath.Should().Be("old-name.txt");
        files[0].RepoRelativePath.Should().Be("new-name.txt");
    }
}
