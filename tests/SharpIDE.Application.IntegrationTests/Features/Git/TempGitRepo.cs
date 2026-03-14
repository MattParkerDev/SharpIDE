using System.Diagnostics;

namespace SharpIDE.Application.IntegrationTests.Features.Git;

internal sealed class TempGitRepo : IDisposable
{
    public string RootPath { get; }

    private TempGitRepo(string rootPath)
    {
        RootPath = rootPath;
    }

    public static Task<TempGitRepo> CreateAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"sharpide-git-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var repo = new TempGitRepo(rootPath);
        repo.Git("init");
        repo.Git("checkout -b main");
        repo.Git("config user.email test@example.com");
        repo.Git("config user.name sharpide-tests");
        return Task.FromResult(repo);
    }

    public string WriteFile(string relativePath, string contents)
    {
        var absolutePath = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, Normalize(contents));
        return absolutePath;
    }

    public string Git(string arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("git", $"-C \"{RootPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start git {arguments}.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode is not 0)
        {
            throw new InvalidOperationException($"git {arguments} failed:{Environment.NewLine}{stdErr}");
        }

        return stdOut.Replace("\r\n", "\n");
    }

    public string Commit(
        string message,
        string? authorName = null,
        string? authorEmail = null,
        DateTimeOffset? authoredAt = null,
        DateTimeOffset? committedAt = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = authorName ?? "sharpide-tests",
            ["GIT_AUTHOR_EMAIL"] = authorEmail ?? "test@example.com",
            ["GIT_COMMITTER_NAME"] = authorName ?? "sharpide-tests",
            ["GIT_COMMITTER_EMAIL"] = authorEmail ?? "test@example.com"
        };

        if (authoredAt is not null)
        {
            environment["GIT_AUTHOR_DATE"] = authoredAt.Value.ToString("O");
        }

        if (committedAt is not null)
        {
            environment["GIT_COMMITTER_DATE"] = committedAt.Value.ToString("O");
        }

        Git($"commit -m \"{message}\"", environment);
        return Git("rev-parse HEAD").Trim();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
        }
    }

    private static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        return normalized.EndsWith('\n') ? normalized : normalized + '\n';
    }
}
