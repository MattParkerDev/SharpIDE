using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitServiceHistoryActionTests
{
    private readonly GitService _gitService = new(new IdeOpenTabsFileManager(NullLogger<IdeOpenTabsFileManager>.Instance));

    [Fact]
    public async Task GetCommitCapabilities_TracksHeadUndoAndPushedState()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        var initialCommit = repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");

        repo.WriteFile("sample.txt", "beta");
        repo.Git("add sample.txt");
        var localCommit = repo.Commit("local");

        var pushedCapabilities = await _gitService.GetCommitCapabilities(repo.RootPath, initialCommit, TestContext.Current.CancellationToken);
        var localCapabilities = await _gitService.GetCommitCapabilities(repo.RootPath, localCommit, TestContext.Current.CancellationToken);

        pushedCapabilities.IsPushedToUpstream.Should().BeTrue();
        pushedCapabilities.CanEditMessage.Should().BeFalse();
        localCapabilities.IsHeadCommit.Should().BeTrue();
        localCapabilities.CanUndoCommit.Should().BeTrue();
        localCapabilities.CanEditMessage.Should().BeTrue();
    }

    [Fact]
    public async Task CreateBranchFromCommit_AndCreateTagAtCommit_CreateRefs()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        var commitSha = repo.Commit("initial");

        await _gitService.CreateBranchFromCommit(repo.RootPath, commitSha, "feature/from-commit", TestContext.Current.CancellationToken);
        await _gitService.CreateTagAtCommit(repo.RootPath, commitSha, "v-next", TestContext.Current.CancellationToken);

        repo.Git("branch --show-current").Trim().Should().Be("feature/from-commit");
        repo.Git("rev-parse refs/tags/v-next").Trim().Should().Be(commitSha);
    }

    [Fact]
    public async Task BuildCommitPatchText_ConcatenatesCommitsInProvidedOrder()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        var firstCommit = repo.Commit("first");
        repo.WriteFile("sample.txt", "beta");
        repo.Git("add sample.txt");
        var secondCommit = repo.Commit("second");

        var patchText = await _gitService.BuildCommitPatchText(repo.RootPath, [firstCommit, secondCommit], TestContext.Current.CancellationToken);

        patchText.Should().Contain("Subject: [PATCH] first");
        patchText.Should().Contain("Subject: [PATCH] second");
        patchText.IndexOf("Subject: [PATCH] first", StringComparison.Ordinal)
            .Should().BeLessThan(patchText.IndexOf("Subject: [PATCH] second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildCommitFilesPatchText_AndApplyCommitFiles_OnlyTouchesSelectedFiles()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("a.txt", "alpha");
        repo.WriteFile("b.txt", "bravo");
        repo.Git("add a.txt b.txt");
        repo.Commit("initial");

        repo.WriteFile("a.txt", "alpha changed");
        repo.WriteFile("b.txt", "bravo changed");
        repo.Git("add a.txt b.txt");
        var commitSha = repo.Commit("update both");

        var patchText = await _gitService.BuildCommitFilesPatchText(repo.RootPath, commitSha, ["a.txt"], TestContext.Current.CancellationToken);
        patchText.Should().Contain("a.txt");
        patchText.Should().NotContain("b.txt");

        repo.Git("reset --hard HEAD~1");
        await _gitService.ApplyCommitFiles(repo.RootPath, commitSha, ["a.txt"], GitHistoricalFileApplyMode.CherryPick, TestContext.Current.CancellationToken);
        File.ReadAllText(Path.Combine(repo.RootPath, "a.txt")).Should().Contain("alpha changed");
        File.ReadAllText(Path.Combine(repo.RootPath, "b.txt")).Should().Contain("bravo");

        repo.Git("add a.txt");
        repo.Git("commit -m \"applied selected file\"");
        await _gitService.ApplyCommitFiles(repo.RootPath, commitSha, ["a.txt"], GitHistoricalFileApplyMode.Revert, TestContext.Current.CancellationToken);
        File.ReadAllText(Path.Combine(repo.RootPath, "a.txt")).Should().Contain("alpha");
    }

    [Fact]
    public async Task GetCommitFileWorkingTreeDiffView_UsesCommitContentAsBase()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        var commitSha = repo.Commit("initial");
        var filePath = repo.WriteFile("sample.txt", "beta");

        var diffView = await _gitService.GetCommitFileWorkingTreeDiffView(new GitCommitWorkingTreeDiffRequest
        {
            RepoRootPath = repo.RootPath,
            CommitSha = commitSha,
            RepoRelativePath = "sample.txt"
        }, TestContext.Current.CancellationToken);

        diffView.BaseDisplayText.Should().Contain("alpha");
        diffView.CurrentDisplayText.Should().Contain("beta");
        diffView.AbsolutePath.Should().Be(filePath);
        diffView.CanEditCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task EditCommitMessage_HeadCommit_AmendsMessage()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        var commitSha = repo.Commit("initial");

        await _gitService.EditCommitMessage(repo.RootPath, commitSha, "amended", TestContext.Current.CancellationToken);

        repo.Git("log -1 --format=%s").Trim().Should().Be("amended");
    }

    [Fact]
    public async Task EditCommitMessage_NonHeadUnpushed_RewritesTargetCommit()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.WriteFile("sample.txt", "beta");
        repo.Git("add sample.txt");
        var targetCommit = repo.Commit("target");
        repo.WriteFile("sample.txt", "charlie");
        repo.Git("add sample.txt");
        repo.Commit("latest");

        await _gitService.EditCommitMessage(repo.RootPath, targetCommit, "target rewritten", TestContext.Current.CancellationToken);

        repo.Git("log --format=%s -3").Replace("\r\n", "\n").Should().StartWith("latest\ntarget rewritten\ninitial");
    }

    [Fact]
    public async Task EditCommitMessage_PushedCommit_Throws()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "alpha");
        repo.Git("add sample.txt");
        var pushedCommit = repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");

        repo.WriteFile("sample.txt", "beta");
        repo.Git("add sample.txt");
        repo.Commit("local");

        var act = () => _gitService.EditCommitMessage(repo.RootPath, pushedCommit, "rewritten", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not pushed*");
    }

    private static void InitBare(string path)
    {
        RunGitRaw($"init --bare \"{path}\"");
    }

    private static string RunGitRaw(string arguments, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start git {arguments}.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!allowFailure && process.ExitCode is not 0)
        {
            throw new InvalidOperationException($"git {arguments} failed:{Environment.NewLine}{stdErr}");
        }

        return stdOut.Replace("\r\n", "\n");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string PathValue { get; }

        private TempDirectory(string pathValue)
        {
            PathValue = pathValue;
        }

        public static TempDirectory Create()
        {
            var path = Path.Combine(Path.GetTempPath(), $"sharpide-git-tests-dir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(PathValue, recursive: true);
            }
            catch
            {
            }
        }
    }
}
