using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitServiceRefActionsTests
{
    private readonly GitService _gitService = new(new IdeOpenTabsFileManager(NullLogger<IdeOpenTabsFileManager>.Instance));

    [Fact]
    public async Task GetRepositoryRefs_MapsUpstreamAndTagRemoteMetadata()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");

        repo.Git("checkout -b feature/tracked");
        repo.WriteFile("tracked.txt", "tracked");
        repo.Git("add tracked.txt");
        repo.Commit("tracked");
        repo.Git("push -u origin feature/tracked");

        repo.Git("checkout main");
        repo.Git("checkout -b feature/local");
        repo.Git("checkout feature/tracked");
        repo.Git("tag v1");
        repo.Git("tag local-only");
        repo.Git("push origin v1");

        var refs = await _gitService.GetRepositoryRefs(repo.RootPath, TestContext.Current.CancellationToken);

        var headNode = refs.Single(node => node.Kind == GitRefKind.Head);
        headNode.ShortName.Should().Be("feature/tracked");

        var localNode = refs.Single(node => node.DisplayName == "Local");
        localNode.Children.Single(node => node.ShortName == "feature/tracked").UpstreamRefName.Should().Be("refs/remotes/origin/feature/tracked");
        localNode.Children.Single(node => node.ShortName == "feature/local").UpstreamRefName.Should().BeNull();

        var tagsNode = refs.Single(node => node.DisplayName == "Tags");
        tagsNode.Children.Single(node => node.ShortName == "v1").PreferredRemoteName.Should().Be("origin");
        tagsNode.Children.Single(node => node.ShortName == "v1").ExistsOnPreferredRemote.Should().BeTrue();
        tagsNode.Children.Single(node => node.ShortName == "local-only").ExistsOnPreferredRemote.Should().BeFalse();
    }

    [Fact]
    public async Task CheckoutRef_LocalBranch_ChecksOutBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git("checkout -b feature/demo");
        repo.Git("checkout main");

        await _gitService.CheckoutRef(repo.RootPath, "refs/heads/feature/demo", TestContext.Current.CancellationToken);

        repo.Git("branch --show-current").Trim().Should().Be("feature/demo");
    }

    [Fact]
    public async Task CheckoutRef_Tag_LeavesDetachedHead()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git("tag v1");

        await _gitService.CheckoutRef(repo.RootPath, "refs/tags/v1", TestContext.Current.CancellationToken);

        repo.Git("symbolic-ref -q HEAD", allowFailure: true).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateBranchFromRef_LocalBranch_CreatesAndChecksOutBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");

        await _gitService.CreateBranchFromRef(repo.RootPath, "refs/heads/main", "feature/local", TestContext.Current.CancellationToken);

        repo.Git("branch --show-current").Trim().Should().Be("feature/local");
    }

    [Fact]
    public async Task CreateBranchFromRef_RemoteBranch_CreatesTrackingBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");
        repo.Git("checkout -b feature/remote");
        repo.WriteFile("remote.txt", "remote");
        repo.Git("add remote.txt");
        repo.Commit("remote");
        repo.Git("push -u origin feature/remote");
        repo.Git("checkout main");

        await _gitService.CreateBranchFromRef(repo.RootPath, "refs/remotes/origin/feature/remote", "feature/local-copy", TestContext.Current.CancellationToken);

        repo.Git("branch --show-current").Trim().Should().Be("feature/local-copy");
        repo.Git("rev-parse --abbrev-ref --symbolic-full-name @{u}").Trim().Should().Be("origin/feature/remote");
    }

    [Fact]
    public async Task RenameLocalBranch_WithoutUpstream_RenamesBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git("checkout -b feature/old");

        await _gitService.RenameLocalBranch(repo.RootPath, "refs/heads/feature/old", "feature/new", TestContext.Current.CancellationToken);

        repo.Git("branch --show-current").Trim().Should().Be("feature/new");
    }

    [Fact]
    public async Task RenameLocalBranch_WithUpstream_Throws()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");

        var act = () => _gitService.RenameLocalBranch(repo.RootPath, "refs/heads/main", "renamed-main", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Tracked branches cannot be renamed*");
    }

    [Fact]
    public async Task DeleteLocalBranch_RemovesBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git("checkout -b feature/delete");
        repo.Git("checkout main");

        await _gitService.DeleteLocalBranch(repo.RootPath, "refs/heads/feature/delete", TestContext.Current.CancellationToken);

        repo.Git("branch --list feature/delete").Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task MergeRefIntoCurrent_MergesSelectedBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");
        repo.Git("checkout -b feature");
        repo.WriteFile("feature.txt", "feature");
        repo.Git("add feature.txt");
        repo.Commit("feature");
        repo.Git("checkout main");

        await _gitService.MergeRefIntoCurrent(repo.RootPath, "refs/heads/feature", TestContext.Current.CancellationToken);

        File.ReadAllText(Path.Combine(repo.RootPath, "feature.txt")).Should().Contain("feature");
    }

    [Fact]
    public async Task RebaseCurrentBranchOnto_RebasesCurrentBranch()
    {
        using var repo = await TempGitRepo.CreateAsync();
        repo.WriteFile("base.txt", "base");
        repo.Git("add base.txt");
        repo.Commit("initial");

        repo.Git("checkout -b feature");
        repo.WriteFile("feature.txt", "feature");
        repo.Git("add feature.txt");
        repo.Commit("feature");

        repo.Git("checkout main");
        repo.WriteFile("main.txt", "main");
        repo.Git("add main.txt");
        var mainCommit = repo.Commit("main");

        repo.Git("checkout feature");

        await _gitService.RebaseCurrentBranchOnto(repo.RootPath, "refs/heads/main", TestContext.Current.CancellationToken);

        repo.Git("merge-base HEAD main").Trim().Should().Be(mainCommit);
    }

    [Fact]
    public async Task UpdateCurrentBranch_FetchesAndPullsLatestCommit()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        using var clone = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");

        CloneRepository(remote.PathValue, clone.PathValue);
        RunGit(clone.PathValue, "config user.email test@example.com");
        RunGit(clone.PathValue, "config user.name sharpide-tests");
        File.WriteAllText(Path.Combine(clone.PathValue, "from-clone.txt"), "clone\n");
        RunGit(clone.PathValue, "add from-clone.txt");
        RunGit(clone.PathValue, "commit -m \"clone change\"");
        var cloneCommit = RunGit(clone.PathValue, "rev-parse HEAD").Trim();
        RunGit(clone.PathValue, "push origin main");

        await _gitService.UpdateCurrentBranch(repo.RootPath, TestContext.Current.CancellationToken);

        repo.Git("rev-parse HEAD").Trim().Should().Be(cloneCommit);
        File.ReadAllText(Path.Combine(repo.RootPath, "from-clone.txt")).Should().Contain("clone");
    }

    [Fact]
    public async Task PushCurrentBranch_PushesCommitToRemote()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");

        repo.WriteFile("sample.txt", "updated");
        repo.Git("add sample.txt");
        var commitSha = repo.Commit("update");

        await _gitService.PushCurrentBranch(repo.RootPath, TestContext.Current.CancellationToken);

        RunGitRaw($"--git-dir=\"{remote.PathValue}\" rev-parse refs/heads/main").Trim().Should().Be(commitSha);
    }

    [Fact]
    public async Task PushTag_AndDeleteRemoteTag_OperateOnPreferredRemote()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var remote = TempDirectory.Create();
        InitBare(remote.PathValue);

        repo.WriteFile("sample.txt", "base");
        repo.Git("add sample.txt");
        repo.Commit("initial");
        repo.Git($"remote add origin \"{remote.PathValue}\"");
        repo.Git("push -u origin main");
        repo.Git("tag v1");

        await _gitService.PushTag(repo.RootPath, "refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);
        RunGitRaw($"--git-dir=\"{remote.PathValue}\" rev-parse refs/tags/v1").Trim().Should().NotBeEmpty();

        await _gitService.DeleteRemoteTag(repo.RootPath, "refs/tags/v1", cancellationToken: TestContext.Current.CancellationToken);
        RunGitRaw($"--git-dir=\"{remote.PathValue}\" show-ref --verify refs/tags/v1", allowFailure: true).Trim().Should().BeEmpty();
    }

    private static void InitBare(string path)
    {
        RunGitRaw($"init --bare \"{path}\"");
    }

    private static void CloneRepository(string remotePath, string targetPath)
    {
        RunGitRaw($"clone \"{remotePath}\" \"{targetPath}\"");
    }

    private static string RunGit(string workingDirectory, string arguments, bool allowFailure = false)
    {
        return RunGitRaw($"-C \"{workingDirectory}\" {arguments}", allowFailure);
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

        private TempDirectory(string path)
        {
            PathValue = path;
        }

        public static TempDirectory Create()
        {
            var path = Path.Combine(Path.GetTempPath(), $"sharpide-git-tests-{Guid.NewGuid():N}");
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
