using SharpIDE.Application.Features.Git;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

public class GitRepositoryMonitorTests
{
    [Fact]
    public async Task Start_RemoteTrackingRefChange_RaisesRepositoryChanged()
    {
        using var repo = await TempGitRepo.CreateAsync();
        using var monitor = new GitRepositoryMonitor();
        var repositoryChangedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteRefPath = Path.Combine(repo.RootPath, ".git", "refs", "remotes", "origin", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(remoteRefPath)!);

        monitor.RepositoryChanged.Subscribe(() =>
        {
            repositoryChangedTcs.TrySetResult();
            return Task.CompletedTask;
        });

        monitor.Start(repo.RootPath, Path.Combine(repo.RootPath, ".git"));

        for (var attempt = 0; attempt < 10 && !repositoryChangedTcs.Task.IsCompleted; attempt++)
        {
            File.WriteAllText(remoteRefPath, $"{attempt:D40}\n");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        await repositoryChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
