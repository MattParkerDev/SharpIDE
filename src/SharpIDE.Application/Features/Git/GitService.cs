using CliWrap;
using CliWrap.Buffered;
using LibGit2Sharp;
using SharpIDE.Application;
using SharpIDE.Application.Features.FilePersistence;
using System.Diagnostics;

namespace SharpIDE.Application.Features.Git;

public class GitService(IdeOpenTabsFileManager openTabsFileManager)
{
    private const string GitEmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private readonly GitSelectivePatchBuilder _patchBuilder = new();
    private readonly IdeOpenTabsFileManager _openTabsFileManager = openTabsFileManager;
    private readonly record struct GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

    public Task<GitSnapshot> GetSnapshot(string solutionFilePathOrDirectory, int commitCount = 50, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetSnapshotCore(solutionFilePathOrDirectory, commitCount), cancellationToken);
    }

    public Task<IReadOnlyList<GitRefNode>> GetRepositoryRefs(string solutionFilePathOrDirectory, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetRepositoryRefsCore(solutionFilePathOrDirectory), cancellationToken);
    }

    public async Task<GitHistoryPage> GetHistoryPage(string repoRoot, GitHistoryQuery query, CancellationToken cancellationToken = default)
    {
        using var repo = OpenRepository(repoRoot);
        if (!query.IncludeAllRefs && string.IsNullOrWhiteSpace(query.RefName))
        {
            throw new InvalidOperationException("A ref name is required when loading scoped git history.");
        }

        var effectiveTake = Math.Max(1, query.Take);
        var effectiveSkip = Math.Max(0, query.Skip);
        var batchSize = Math.Max(500, effectiveSkip + effectiveTake * 3);
        var matchedRows = new List<GitHistoryRow>();
        var scannedCommits = 0;
        var hasMoreRawCommits = true;
        var configuredIdentity = ResolveConfiguredIdentity(repo);
        var primaryBranchCommits = !query.IncludeAllRefs && !string.IsNullOrWhiteSpace(query.RefName)
            ? await LoadPrimaryBranchCommits(repo, query.RefName, cancellationToken)
            : null;

        while (matchedRows.Count < effectiveSkip + effectiveTake + 1 && hasMoreRawCommits)
        {
            var rawRows = await LoadGraphHistoryRows(
                repo,
                query.RefName,
                query.IncludeAllRefs,
                scannedCommits,
                batchSize,
                configuredIdentity,
                primaryBranchCommits,
                cancellationToken);
            if (rawRows.Count is 0)
            {
                hasMoreRawCommits = false;
                break;
            }

            scannedCommits += rawRows.Count;
            hasMoreRawCommits = rawRows.Count == batchSize;

            IReadOnlySet<string>? allowedShas = query.SearchMode switch
            {
                GitHistorySearchMode.CommitMetadata when !string.IsNullOrWhiteSpace(query.SearchTerm)
                    => await LoadCommitMetadataMatches(repo.Info.WorkingDirectory, query.RefName, query.IncludeAllRefs, query.SearchTerm, scannedCommits, cancellationToken),
                GitHistorySearchMode.Paths when !string.IsNullOrWhiteSpace(query.SearchTerm)
                    => await LoadPathMatches(repo.Info.WorkingDirectory, query.RefName, query.IncludeAllRefs, query.SearchTerm, scannedCommits, cancellationToken),
                _ => null
            };

            var newMatches = allowedShas is null
                ? rawRows
                : rawRows.Where(row => allowedShas.Contains(row.Sha)).ToList();
            matchedRows.AddRange(newMatches.Where(row => matchedRows.All(existing => !string.Equals(existing.Sha, row.Sha, StringComparison.Ordinal))));

            if (!hasMoreRawCommits)
            {
                break;
            }
        }

        var pagedRows = matchedRows
            .Skip(effectiveSkip)
            .Take(effectiveTake)
            .ToList();
        var hasMore = matchedRows.Count > effectiveSkip + effectiveTake || hasMoreRawCommits;

        return new GitHistoryPage
        {
            Rows = pagedRows,
            HasMore = hasMore
        };
    }

    public async Task<GitCommitDetails> GetCommitDetails(string repoRoot, string commitSha, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteGitBufferedAsync(
            repoRoot,
            ["show", "-s", "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%s%x1f%B%x1e", commitSha],
            cancellationToken);
        EnsureSuccess(result, "Failed to load commit details.");

        var record = ParseNullSeparatedRecords(result.StandardOutput).FirstOrDefault()
            ?? throw new InvalidOperationException($"Commit '{commitSha}' was not found.");
        var sha = record[0];
        var authoredAt = DateTimeOffset.Parse(record[4]);
        var committedAt = DateTimeOffset.Parse(record[5]);
        var parentShas = string.IsNullOrWhiteSpace(record[1])
            ? []
            : record[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fullMessage = string.Join('\u001f', record.Skip(7)).TrimEnd();

        return new GitCommitDetails
        {
            Sha = sha,
            ShortSha = sha[..Math.Min(8, sha.Length)],
            FullMessage = fullMessage,
            Subject = record[6],
            AuthorName = record[2],
            AuthorEmail = record[3],
            AuthoredAt = authoredAt,
            CommittedAt = committedAt,
            FriendlyTimestamp = FormatFriendlyTimestamp(authoredAt),
            FriendlyCommittedTimestamp = FormatFriendlyTimestamp(committedAt),
            ParentShas = parentShas
        };
    }

    public async Task<IReadOnlyList<GitCommitChangedFile>> GetCommitChangedFiles(string repoRoot, string commitSha, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteGitBufferedAsync(
            repoRoot,
            ["diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-M", commitSha],
            cancellationToken);
        EnsureSuccess(result, "Failed to load commit file changes.");

        var files = new List<GitCommitChangedFile>();
        foreach (var line in result.StandardOutput.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;

            var statusCode = parts[0];
            if (statusCode.StartsWith("R", StringComparison.Ordinal))
            {
                if (parts.Length < 3) continue;
                files.Add(new GitCommitChangedFile
                {
                    RepoRelativePath = NormalizeRepositoryRelativePath(parts[2]),
                    OldRepoRelativePath = NormalizeRepositoryRelativePath(parts[1]),
                    StatusCode = "R",
                    DisplayPath = $"{NormalizeRepositoryRelativePath(parts[1])} -> {NormalizeRepositoryRelativePath(parts[2])}"
                });
                continue;
            }

            files.Add(new GitCommitChangedFile
            {
                RepoRelativePath = NormalizeRepositoryRelativePath(parts[^1]),
                OldRepoRelativePath = null,
                StatusCode = statusCode[..1],
                DisplayPath = NormalizeRepositoryRelativePath(parts[^1])
            });
        }

        return files;
    }

    public async Task<GitDiffViewModel> GetCommitFileDiffView(GitCommitFileDiffRequest request, CancellationToken cancellationToken = default)
    {
        var details = await GetCommitDetails(request.RepoRootPath, request.CommitSha, cancellationToken);
        var parentSha = details.ParentShas.FirstOrDefault();
        var currentRevisionPath = $"{request.CommitSha}:{request.RepoRelativePath}";
        var baseRevisionPath = parentSha is null
            ? null
            : $"{parentSha}:{request.OldRepoRelativePath ?? request.RepoRelativePath}";

        var currentText = await GetRevisionFileText(request.RepoRootPath, request.RepoRelativePath, currentRevisionPath, cancellationToken);
        var baseText = baseRevisionPath is null
            ? string.Empty
            : await GetRevisionFileText(request.RepoRootPath, request.OldRepoRelativePath ?? request.RepoRelativePath, baseRevisionPath, cancellationToken);

        string patchText;
        if (parentSha is null)
        {
            patchText = await ExecuteGitForText(
                request.RepoRootPath,
                ["diff", "--no-color", "--no-ext-diff", "--unified=3", "--find-renames", GitEmptyTreeSha, request.CommitSha, "--", request.RepoRelativePath],
                cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.OldRepoRelativePath) && !string.Equals(request.OldRepoRelativePath, request.RepoRelativePath, StringComparison.Ordinal))
        {
            patchText = await ExecuteGitForText(
                request.RepoRootPath,
                ["diff", "--no-color", "--no-ext-diff", "--unified=3", "--find-renames", parentSha, request.CommitSha, "--", request.OldRepoRelativePath, request.RepoRelativePath],
                cancellationToken);
        }
        else
        {
            patchText = await ExecuteGitForText(
                request.RepoRootPath,
                ["diff", "--no-color", "--no-ext-diff", "--unified=3", "--find-renames", parentSha, request.CommitSha, "--", request.RepoRelativePath],
                cancellationToken);
        }

        var absolutePath = Path.Combine(request.RepoRootPath, request.RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return _patchBuilder.BuildCanonicalViewModel(
            request.RepoRelativePath,
            absolutePath,
            GitDiffMode.Historical,
            parentSha is null ? "Empty" : (request.OldRepoRelativePath ?? request.RepoRelativePath),
            $"{request.CommitSha[..Math.Min(8, request.CommitSha.Length)]}:{request.RepoRelativePath}",
            baseText,
            currentText,
            canEditCurrent: false);
    }

    public async Task<GitDiffViewModel> GetStashFileDiffView(GitStashFileDiffRequest request, CancellationToken cancellationToken = default)
    {
        var absolutePath = Path.Combine(request.RepoRootPath, request.RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));

        string baseLabel;
        string currentLabel;
        string baseText;
        string currentText;

        if (request.ContentKind is GitStashFileContentKind.UntrackedSnapshot)
        {
            baseLabel = "Empty";
            currentLabel = $"{request.StashRef}^3:{request.RepoRelativePath}";
            baseText = string.Empty;
            currentText = await GetRevisionFileText(
                request.RepoRootPath,
                request.RepoRelativePath,
                $"{request.StashRef}^3:{request.RepoRelativePath}",
                cancellationToken);
        }
        else
        {
            var basePath = request.OldRepoRelativePath ?? request.RepoRelativePath;
            baseLabel = $"{request.StashRef}^1:{basePath}";
            currentLabel = request.StatusCode.StartsWith("D", StringComparison.Ordinal)
                ? "Deleted in stash"
                : $"{request.StashRef}:{request.RepoRelativePath}";
            baseText = await GetRevisionFileText(
                request.RepoRootPath,
                basePath,
                $"{request.StashRef}^1:{basePath}",
                cancellationToken);
            currentText = request.StatusCode.StartsWith("D", StringComparison.Ordinal)
                ? string.Empty
                : await GetRevisionFileText(
                    request.RepoRootPath,
                    request.RepoRelativePath,
                    $"{request.StashRef}:{request.RepoRelativePath}",
                    cancellationToken);
        }

        return _patchBuilder.BuildCanonicalViewModel(
            request.RepoRelativePath,
            absolutePath,
            GitDiffMode.Historical,
            baseLabel,
            currentLabel,
            baseText,
            currentText,
            canEditCurrent: false);
    }

    public Task StagePaths(string repoRoot, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var relativePaths = ToRepositoryRelativePaths(repo, paths).ToList();
            if (relativePaths.Count is 0) return;
            Commands.Stage(repo, relativePaths);
        }, cancellationToken);
    }

    public Task UnstagePaths(string repoRoot, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var relativePaths = ToRepositoryRelativePaths(repo, paths).ToList();
            if (relativePaths.Count is 0) return;
            if (repo.Head.Tip is null)
            {
                foreach (var relativePath in relativePaths)
                {
                    repo.Index.Remove(relativePath);
                }

                repo.Index.Write();
                return;
            }

            Commands.Unstage(repo, relativePaths);
        }, cancellationToken);
    }

    public Task Commit(string repoRoot, string message, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var signature = ResolveSignature(repo);
            repo.Commit(message, signature, signature);
        }, cancellationToken);
    }

    public async Task Push(string repoRoot, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteGitBufferedAsync(repoRoot, ["push"], cancellationToken);
        EnsureSuccess(result, "Push failed.");
    }

    public Task Reset(string repoRoot, string commitSha, ResetMode mode, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var commit = repo.Lookup<Commit>(commitSha) ?? throw new InvalidOperationException($"Commit '{commitSha}' was not found.");
            repo.Reset(mode, commit);
        }, cancellationToken);
    }

    public Task CheckoutCommit(string repoRoot, string commitSha, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var commit = repo.Lookup<Commit>(commitSha) ?? throw new InvalidOperationException($"Commit '{commitSha}' was not found.");
            Commands.Checkout(repo, commit);
        }, cancellationToken);
    }

    public Task RevertCommit(string repoRoot, string commitSha, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var commit = repo.Lookup<Commit>(commitSha) ?? throw new InvalidOperationException($"Commit '{commitSha}' was not found.");
            var signature = ResolveSignature(repo);
            var result = repo.Revert(commit, signature, new RevertOptions());
            if (result.Status is RevertStatus.Conflicts)
            {
                throw new InvalidOperationException("Revert produced conflicts. Resolve the git conflict state before continuing.");
            }

            if (result.Status is not RevertStatus.Reverted and not RevertStatus.NothingToRevert)
            {
                throw new InvalidOperationException($"Revert failed with status '{result.Status}'.");
            }
        }, cancellationToken);
    }

    public Task CherryPickCommit(string repoRoot, string commitSha, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var repo = OpenRepository(repoRoot);
            var commit = repo.Lookup<Commit>(commitSha) ?? throw new InvalidOperationException($"Commit '{commitSha}' was not found.");
            var signature = ResolveSignature(repo);
            var result = repo.CherryPick(commit, signature, new CherryPickOptions());
            if (result.Status is CherryPickStatus.Conflicts)
            {
                throw new InvalidOperationException("Cherry-pick produced conflicts. Resolve the git conflict state before continuing.");
            }

            if (result.Status is not CherryPickStatus.CherryPicked)
            {
                throw new InvalidOperationException($"Cherry-pick failed with status '{result.Status}'.");
            }
        }, cancellationToken);
    }

    public async Task SelectiveStash(string repoRoot, IEnumerable<string> paths, string message, bool includeUntracked, CancellationToken cancellationToken = default)
    {
        using var repo = OpenRepository(repoRoot);
        var relativePaths = ToRepositoryRelativePaths(repo, paths).ToList();
        if (relativePaths.Count is 0) throw new InvalidOperationException("At least one file must be selected.");

        var result = await ExecuteGitBufferedAsync(
            repo.Info.WorkingDirectory,
            BuildSelectiveStashArguments(relativePaths, message, includeUntracked),
            cancellationToken);
        EnsureSuccess(result, "Selective stash failed.");
    }

    public async Task ApplyStash(string repoRoot, string stashRef, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteGitBufferedAsync(repoRoot, ["stash", "apply", stashRef], cancellationToken);
        EnsureSuccess(result, "Failed to apply stash.");
    }

    public async Task ApplyStashFileChanges(GitStashFileDiffRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ContentKind is GitStashFileContentKind.UntrackedSnapshot)
        {
            var result = await ExecuteGitBufferedAsync(
                request.RepoRootPath,
                BuildRestoreArguments(source: $"{request.StashRef}^3", staged: false, worktree: true, [request.RepoRelativePath]),
                cancellationToken);
            EnsureSuccess(result, "Failed to apply stashed file changes.");
            return;
        }

        if (request.StatusCode.StartsWith("D", StringComparison.Ordinal))
        {
            DeleteWorktreePath(request.RepoRootPath, request.RepoRelativePath);
            return;
        }

        if (request.StatusCode.StartsWith("R", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(request.OldRepoRelativePath) &&
            !string.Equals(request.OldRepoRelativePath, request.RepoRelativePath, StringComparison.Ordinal))
        {
            DeleteWorktreePath(request.RepoRootPath, request.OldRepoRelativePath);
        }

        var restoreResult = await ExecuteGitBufferedAsync(
            request.RepoRootPath,
            BuildRestoreArguments(source: request.StashRef, staged: false, worktree: true, [request.RepoRelativePath]),
            cancellationToken);
        EnsureSuccess(restoreResult, "Failed to apply stashed file changes.");
    }

    public async Task PopStash(string repoRoot, string stashRef, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteGitBufferedAsync(repoRoot, ["stash", "pop", stashRef], cancellationToken);
        EnsureSuccess(result, "Failed to pop stash.");
    }

    public async Task DropStash(string repoRoot, string stashRef, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteGitBufferedAsync(repoRoot, ["stash", "drop", stashRef], cancellationToken);
        EnsureSuccess(result, "Failed to drop stash.");
    }

    public async Task DiscardPaths(string repoRoot, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        using var repo = OpenRepository(repoRoot);
        var relativePaths = ToRepositoryRelativePaths(repo, paths).ToList();
        if (relativePaths.Count is 0) return;

        var restoreFromHead = new List<string>();
        var removePaths = new List<string>();
        foreach (var relativePath in relativePaths)
        {
            if (ExistsInHead(repo.Head.Tip, relativePath))
            {
                restoreFromHead.Add(relativePath);
            }
            else
            {
                removePaths.Add(relativePath);
            }
        }

        if (restoreFromHead.Count > 0)
        {
            var restoreResult = await ExecuteGitBufferedAsync(
                repo.Info.WorkingDirectory,
                BuildRestoreArguments(source: "HEAD", staged: true, worktree: true, restoreFromHead),
                cancellationToken);
            EnsureSuccess(restoreResult, "Failed to discard file changes.");
        }

        if (removePaths.Count is 0) return;

        var indexChanged = false;
        foreach (var relativePath in removePaths)
        {
            if (repo.Index[relativePath] is not null)
            {
                repo.Index.Remove(relativePath);
                indexChanged = true;
            }

            var absolutePath = Path.Combine(repo.Info.WorkingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }

        if (indexChanged)
        {
            repo.Index.Write();
        }
    }

    public async Task DiscardUnstagedPaths(string repoRoot, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        using var repo = OpenRepository(repoRoot);
        var relativePaths = ToRepositoryRelativePaths(repo, paths).ToList();
        if (relativePaths.Count is 0) return;

        var result = await ExecuteGitBufferedAsync(
            repo.Info.WorkingDirectory,
            BuildRestoreArguments(source: null, staged: false, worktree: true, relativePaths),
            cancellationToken);
        EnsureSuccess(result, "Failed to discard unstaged changes.");
    }

    public async Task<GitDiffViewModel?> GetFileDiffView(string absolutePath, CancellationToken cancellationToken = default)
    {
        using var repo = OpenRepositoryForPath(absolutePath);
        var absoluteFullPath = NormalizePath(absolutePath);
        var repoRelativePath = NormalizeRepositoryRelativePath(Path.GetRelativePath(repo.Info.WorkingDirectory, absoluteFullPath));
        var entry = GetCurrentWorkingTreeEntry(repo, absoluteFullPath);
        if (entry is null || entry.Status.HasFlag(GitWorkingTreeStatus.Conflicted))
        {
            return null;
        }

        var currentText = await ReadWorkingTextAsync(absoluteFullPath, cancellationToken);
        if (!entry.IsTracked)
        {
            return _patchBuilder.BuildCanonicalViewModel(
                repoRelativePath,
                absoluteFullPath,
                GitDiffMode.Untracked,
                "Empty",
                "Current version",
                string.Empty,
                currentText,
                canEditCurrent: true);
        }

        var unstagedPatch = await GetDiffPatchText(repo.Info.WorkingDirectory, repoRelativePath, staged: false, unifiedLines: 3, cancellationToken);
        var stagedPatch = await GetDiffPatchText(repo.Info.WorkingDirectory, repoRelativePath, staged: true, unifiedLines: 3, cancellationToken);
        if (string.IsNullOrWhiteSpace(unstagedPatch) && string.IsNullOrWhiteSpace(stagedPatch))
        {
            return null;
        }

        var headText = await GetRevisionFileText(repo.Info.WorkingDirectory, repoRelativePath, $"HEAD:{repoRelativePath}", cancellationToken);
        return _patchBuilder.BuildCanonicalViewModel(
            repoRelativePath,
            absoluteFullPath,
            GitDiffMode.Historical,
            "HEAD",
            "Current version",
            headText,
            currentText,
            canEditCurrent: true);
    }

    public async Task<GitFileContentViewModel> GetFileContentView(string absolutePath, CancellationToken cancellationToken = default)
    {
        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(GetFileContentView)}");
        activity?.SetTag("git.diff.absolute_path", absolutePath);
        using var repo = OpenRepositoryForPath(absolutePath);
        var absoluteFullPath = NormalizePath(absolutePath);
        var repoRelativePath = NormalizeRepositoryRelativePath(Path.GetRelativePath(repo.Info.WorkingDirectory, absoluteFullPath));
        activity?.SetTag("git.diff.repo_relative_path", repoRelativePath);
        var entry = GetCurrentWorkingTreeEntry(repo, absoluteFullPath);
        if (entry is null)
        {
            return await BuildFallbackFileContentView(repo, absoluteFullPath, repoRelativePath, cancellationToken);
        }

        if (entry.Status.HasFlag(GitWorkingTreeStatus.Conflicted))
        {
            return await GetMergeConflictView(repo, absoluteFullPath, repoRelativePath, cancellationToken);
        }

        string workingText;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.ReadWorkingTreeText"))
        {
            workingText = await ReadWorkingTextAsync(absoluteFullPath, cancellationToken);
        }

        if (!entry.IsTracked)
        {
            activity?.SetTag("git.diff.is_tracked", false);
            GitDiffViewModel untrackedDiffView;
            using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.BuildUntrackedDiffView"))
            {
                untrackedDiffView = _patchBuilder.BuildCanonicalViewModel(
                    repoRelativePath,
                    absoluteFullPath,
                    GitDiffMode.Untracked,
                    "Empty",
                    "Current version",
                    string.Empty,
                    workingText,
                    canEditCurrent: true);
            }

            GitDiffActionModel untrackedActions;
            using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.BuildUntrackedActionModel"))
            {
                untrackedActions = _patchBuilder.BuildActionModel(
                    _patchBuilder.CreateUntrackedFileDiff(repoRelativePath, workingText),
                    untrackedDiffView,
                    GitPatchOperationMode.Stage,
                    [GitPatchOperationMode.Stage],
                    GitSelectivePatchBuilder.ActionAnchorSide.Worktree);
            }

            return new GitFileContentViewModel
            {
                Kind = GitFileContentViewKind.Diff,
                AbsolutePath = absoluteFullPath,
                RepoRelativePath = repoRelativePath,
                DiffView = untrackedDiffView,
                UnstagedActions = untrackedActions,
                StagedActions = EmptyActionModel(),
                RowStatesByRowId = new Dictionary<string, GitDiffRowState>(StringComparer.Ordinal)
            };
        }

        activity?.SetTag("git.diff.is_tracked", true);
        string headText;
        string indexText;
        string unstagedPatchText;
        string stagedPatchText;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.LoadTrackedDiffInputs"))
        {
            headText = await GetRevisionFileText(repo.Info.WorkingDirectory, repoRelativePath, $"HEAD:{repoRelativePath}", cancellationToken);
            indexText = await GetRevisionFileText(repo.Info.WorkingDirectory, repoRelativePath, $":{repoRelativePath}", cancellationToken);
            unstagedPatchText = await GetDiffPatchText(repo.Info.WorkingDirectory, repoRelativePath, staged: false, unifiedLines: 3, cancellationToken);
            stagedPatchText = await GetDiffPatchText(repo.Info.WorkingDirectory, repoRelativePath, staged: true, unifiedLines: 3, cancellationToken);
        }

        GitDiffViewModel diffView;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.BuildCanonicalDiffView"))
        {
            diffView = _patchBuilder.BuildCanonicalViewModel(
                repoRelativePath,
                absoluteFullPath,
                GitDiffMode.Historical,
                "HEAD",
                "Current version",
                headText,
                workingText,
                canEditCurrent: true);
        }

        GitDiffActionModel unstagedActions;
        GitDiffActionModel stagedActions;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.BuildActionModels"))
        {
            unstagedActions = string.IsNullOrWhiteSpace(unstagedPatchText)
                ? EmptyActionModel()
                : _patchBuilder.BuildActionModel(
                    _patchBuilder.ParseUnifiedDiff(unstagedPatchText, repoRelativePath),
                    diffView,
                    GitPatchOperationMode.Stage,
                    [GitPatchOperationMode.Stage, GitPatchOperationMode.Revert],
                    GitSelectivePatchBuilder.ActionAnchorSide.Worktree);
            stagedActions = string.IsNullOrWhiteSpace(stagedPatchText)
                ? EmptyActionModel()
                : _patchBuilder.BuildActionModel(
                    _patchBuilder.ParseUnifiedDiff(stagedPatchText, repoRelativePath),
                    diffView,
                    GitPatchOperationMode.Unstage,
                    [GitPatchOperationMode.Unstage],
                    GitSelectivePatchBuilder.ActionAnchorSide.Head);
        }

        GitSelectivePatchBuilder.RowStateProjection rowStateProjection;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.BuildRowStateProjection"))
        {
            rowStateProjection = _patchBuilder.BuildRowStateProjection(
                diffView,
                headText,
                indexText,
                workingText,
                unstagedActions,
                stagedActions);
        }

        return new GitFileContentViewModel
        {
            Kind = GitFileContentViewKind.Diff,
            AbsolutePath = absoluteFullPath,
            RepoRelativePath = repoRelativePath,
            DiffView = diffView,
            UnstagedActions = unstagedActions,
            StagedActions = stagedActions,
            RowStatesByRowId = rowStateProjection.RowStatesByRowId
        };
    }

    private async Task<GitFileContentViewModel> BuildFallbackFileContentView(
        Repository repo,
        string absolutePath,
        string repoRelativePath,
        CancellationToken cancellationToken)
    {
        var currentText = await ReadWorkingTextAsync(absolutePath, cancellationToken);
        var existsInHead = ExistsInHead(repo.Head.Tip, repoRelativePath);

        if (!existsInHead)
        {
            var untrackedDiffView = _patchBuilder.BuildCanonicalViewModel(
                repoRelativePath,
                absolutePath,
                GitDiffMode.Untracked,
                "Empty",
                "Current version",
                string.Empty,
                currentText,
                canEditCurrent: true);

            return new GitFileContentViewModel
            {
                Kind = GitFileContentViewKind.Diff,
                AbsolutePath = absolutePath,
                RepoRelativePath = repoRelativePath,
                DiffView = untrackedDiffView,
                UnstagedActions = EmptyActionModel(),
                StagedActions = EmptyActionModel(),
                RowStatesByRowId = new Dictionary<string, GitDiffRowState>(StringComparer.Ordinal)
            };
        }

        var headText = await GetRevisionFileText(repo.Info.WorkingDirectory, repoRelativePath, $"HEAD:{repoRelativePath}", cancellationToken);
        var diffView = _patchBuilder.BuildCanonicalViewModel(
            repoRelativePath,
            absolutePath,
            GitDiffMode.Historical,
            "HEAD",
            "Current version",
            headText,
            currentText,
            canEditCurrent: true);

        return new GitFileContentViewModel
        {
            Kind = GitFileContentViewKind.Diff,
            AbsolutePath = absolutePath,
            RepoRelativePath = repoRelativePath,
            DiffView = diffView,
            UnstagedActions = EmptyActionModel(),
            StagedActions = EmptyActionModel(),
            RowStatesByRowId = new Dictionary<string, GitDiffRowState>(StringComparer.Ordinal)
        };
    }

    public async Task StageChunk(string filePath, string chunkId, CancellationToken cancellationToken = default)
    {
        await ApplyChunkPatch(filePath, chunkId, GitPatchOperationMode.Stage, cancellationToken);
    }

    public async Task UnstageChunk(string filePath, string chunkId, CancellationToken cancellationToken = default)
    {
        await ApplyChunkPatch(filePath, chunkId, GitPatchOperationMode.Unstage, cancellationToken);
    }

    public async Task RevertChunk(string filePath, string chunkId, CancellationToken cancellationToken = default)
    {
        await ApplyChunkPatch(filePath, chunkId, GitPatchOperationMode.Revert, cancellationToken);
    }

    public async Task StageLines(string filePath, IReadOnlyList<string> lineActionIds, CancellationToken cancellationToken = default)
    {
        await ApplyLinePatch(filePath, lineActionIds, GitPatchOperationMode.Stage, cancellationToken);
    }

    public async Task UnstageLines(string filePath, IReadOnlyList<string> lineActionIds, CancellationToken cancellationToken = default)
    {
        await ApplyLinePatch(filePath, lineActionIds, GitPatchOperationMode.Unstage, cancellationToken);
    }

    public Task SaveDiffEditorText(string absolutePath, string text, CancellationToken cancellationToken = default)
    {
        return File.WriteAllTextAsync(absolutePath, text, cancellationToken);
    }

    public Task SaveMergeConflictCurrent(string absolutePath, string text, CancellationToken cancellationToken = default)
    {
        return File.WriteAllTextAsync(absolutePath, text, cancellationToken);
    }

    public async Task<bool> IsGitCliAvailable(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap("git")
                .WithArguments("--version")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
            return result.ExitCode is 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task ApplyChunkPatch(string filePath, string chunkId, GitPatchOperationMode mode, CancellationToken cancellationToken)
    {
        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(ApplyChunkPatch)}");
        activity?.SetTag("git.diff.absolute_path", filePath);
        activity?.SetTag("git.diff.chunk_id", chunkId);
        activity?.SetTag("git.diff.operation_mode", mode.ToString());
        using var repo = OpenRepositoryForPath(filePath);
        var diff = await GetFreshDiffForMode(repo, filePath, mode, cancellationToken);
        var actionModel = await BuildFreshActionModelForMode(repo, filePath, mode, diff, cancellationToken);
        var chunkAction = actionModel.ChunkActions.SingleOrDefault(action =>
            action.OperationMode == mode &&
            string.Equals(action.ActionChunkId, chunkId, StringComparison.Ordinal));
        if (chunkAction is null || string.IsNullOrWhiteSpace(chunkAction.PatchText))
        {
            throw new InvalidOperationException("The selected chunk is no longer available.");
        }

        var args = BuildApplyArguments(mode, chunkSelection: true);
        BufferedCommandResult result;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.ExecuteChunkPatch"))
        {
            result = await ExecuteGitBufferedAsync(repo.Info.WorkingDirectory, args, cancellationToken, chunkAction.PatchText);
        }

        EnsureSuccess(result, $"Failed to {mode.ToString().ToLowerInvariant()} chunk.");
    }

    private async Task ApplyLinePatch(string filePath, IReadOnlyList<string> lineActionIds, GitPatchOperationMode mode, CancellationToken cancellationToken)
    {
        if (lineActionIds.Count is 0) return;

        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(ApplyLinePatch)}");
        activity?.SetTag("git.diff.absolute_path", filePath);
        activity?.SetTag("git.diff.line_action.count", lineActionIds.Count);
        activity?.SetTag("git.diff.operation_mode", mode.ToString());
        using var repo = OpenRepositoryForPath(filePath);
        var absolutePath = NormalizePath(filePath);
        var repoRelativePath = NormalizeRepositoryRelativePath(Path.GetRelativePath(repo.Info.WorkingDirectory, absolutePath));
        string currentText;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.ReadPatchSourceText"))
        {
            currentText = await ReadWorkingTextAsync(absolutePath, cancellationToken);
        }

        var entry = GetCurrentWorkingTreeEntry(repo, absolutePath);
        if (entry is null)
        {
            throw new InvalidOperationException("This file no longer has a diff.");
        }

        string patchText;
        if (!entry.IsTracked)
        {
            if (mode is not GitPatchOperationMode.Stage)
            {
                throw new InvalidOperationException("Untracked files only support staging.");
            }

            patchText = _patchBuilder.BuildUntrackedLinePatch(repoRelativePath, currentText, lineActionIds);
        }
        else
        {
            var diff = await GetFreshDiffForMode(repo, filePath, mode, cancellationToken);
            using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.BuildLinePatch"))
            {
                patchText = _patchBuilder.BuildLinePatch(diff, lineActionIds, mode);
            }
        }

        if (string.IsNullOrWhiteSpace(patchText))
        {
            throw new InvalidOperationException("The selected lines are no longer available.");
        }

        var args = BuildApplyArguments(mode, chunkSelection: false);
        BufferedCommandResult result;
        using (SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.ExecuteLinePatch"))
        {
            result = await ExecuteGitBufferedAsync(repo.Info.WorkingDirectory, args, cancellationToken, patchText);
        }

        EnsureSuccess(result, $"Failed to {mode.ToString().ToLowerInvariant()} selected lines.");
    }

    private async Task<GitSelectivePatchBuilder.ParsedDiffFile> GetFreshDiffForMode(Repository repo, string filePath, GitPatchOperationMode mode, CancellationToken cancellationToken)
    {
        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(GetFreshDiffForMode)}");
        activity?.SetTag("git.diff.absolute_path", filePath);
        activity?.SetTag("git.diff.operation_mode", mode.ToString());
        var absolutePath = NormalizePath(filePath);
        var repoRelativePath = NormalizeRepositoryRelativePath(Path.GetRelativePath(repo.Info.WorkingDirectory, absolutePath));
        var currentText = await ReadWorkingTextAsync(absolutePath, cancellationToken);
        var entry = GetCurrentWorkingTreeEntry(repo, absolutePath);
        if (entry is null)
        {
            throw new InvalidOperationException("This file no longer has a diff.");
        }

        if (!entry.IsTracked)
        {
            if (mode is not GitPatchOperationMode.Stage)
            {
                throw new InvalidOperationException("Untracked files only support staging.");
            }

            return _patchBuilder.CreateUntrackedFileDiff(repoRelativePath, currentText);
        }

        var staged = mode is GitPatchOperationMode.Unstage;
        var patchText = await GetDiffPatchText(repo.Info.WorkingDirectory, repoRelativePath, staged, unifiedLines: 3, cancellationToken);

        return _patchBuilder.ParseUnifiedDiff(patchText, repoRelativePath);
    }

    private static IReadOnlyList<string> BuildApplyArguments(GitPatchOperationMode mode, bool chunkSelection)
    {
        var args = new List<string> { "apply" };
        if (mode is GitPatchOperationMode.Stage or GitPatchOperationMode.Unstage)
        {
            args.Add("--cached");
        }

        if (mode is GitPatchOperationMode.Unstage or GitPatchOperationMode.Revert)
        {
            args.Add("-R");
        }

        args.Add("--unidiff-zero");

        args.Add("--whitespace=nowarn");
        args.Add("-");
        return args;
    }

    private async Task<GitDiffActionModel> BuildFreshActionModelForMode(
        Repository repo,
        string filePath,
        GitPatchOperationMode mode,
        GitSelectivePatchBuilder.ParsedDiffFile diff,
        CancellationToken cancellationToken)
    {
        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(BuildFreshActionModelForMode)}");
        activity?.SetTag("git.diff.absolute_path", filePath);
        activity?.SetTag("git.diff.operation_mode", mode.ToString());
        var absolutePath = NormalizePath(filePath);
        var repoRelativePath = NormalizeRepositoryRelativePath(Path.GetRelativePath(repo.Info.WorkingDirectory, absolutePath));
        var currentText = await ReadWorkingTextAsync(absolutePath, cancellationToken);
        var entry = GetCurrentWorkingTreeEntry(repo, absolutePath)
            ?? throw new InvalidOperationException("This file no longer has a diff.");

        GitDiffViewModel canonicalView;
        GitSelectivePatchBuilder.ActionAnchorSide anchorSide;

        if (!entry.IsTracked)
        {
            if (mode is not GitPatchOperationMode.Stage)
            {
                throw new InvalidOperationException("Untracked files only support staging.");
            }

            canonicalView = _patchBuilder.BuildCanonicalViewModel(
                repoRelativePath,
                absolutePath,
                GitDiffMode.Untracked,
                "Empty",
                "Current version",
                string.Empty,
                currentText,
                canEditCurrent: true);
            anchorSide = GitSelectivePatchBuilder.ActionAnchorSide.Worktree;
        }
        else
        {
            var headText = await GetRevisionFileText(repo.Info.WorkingDirectory, repoRelativePath, $"HEAD:{repoRelativePath}", cancellationToken);
            canonicalView = _patchBuilder.BuildCanonicalViewModel(
                repoRelativePath,
                absolutePath,
                GitDiffMode.Historical,
                "HEAD",
                "Current version",
                headText,
                currentText,
                canEditCurrent: true);
            anchorSide = mode is GitPatchOperationMode.Unstage
                ? GitSelectivePatchBuilder.ActionAnchorSide.Head
                : GitSelectivePatchBuilder.ActionAnchorSide.Worktree;
        }

        return _patchBuilder.BuildActionModel(
            diff,
            canonicalView,
            mode,
            [mode],
            anchorSide);
    }

    private static GitDiffActionModel EmptyActionModel()
    {
        return new GitDiffActionModel
        {
            LineActions = [],
            ChunkActions = []
        };
    }

    private static IReadOnlyList<GitRefNode> GetRepositoryRefsCore(string solutionFilePathOrDirectory)
    {
        var discoveryPath = Directory.Exists(solutionFilePathOrDirectory)
            ? solutionFilePathOrDirectory
            : Path.GetDirectoryName(solutionFilePathOrDirectory) ?? solutionFilePathOrDirectory;
        var gitPath = Repository.Discover(discoveryPath);
        if (gitPath is null)
        {
            return [];
        }

        using var repo = new Repository(gitPath);
        var currentRefName = repo.Head.CanonicalName;
        var mainRefName = ResolveMainRefName(repo);

        var headNode = new GitRefNode
        {
            DisplayName = "HEAD (Current Branch)",
            RefName = currentRefName,
            Kind = GitRefKind.Head,
            IsSelectable = true,
            IsCurrent = true,
            IsMain = string.Equals(currentRefName, mainRefName, StringComparison.Ordinal),
            Children = []
        };

        var localChildren = repo.Branches
            .Where(branch => !branch.IsRemote)
            .OrderBy(branch => branch.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(branch => CreateBranchNode(branch, mainRefName, currentRefName))
            .ToList();

        var remoteRoot = new Dictionary<string, RemoteTreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in repo.Branches.Where(branch => branch.IsRemote && !branch.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal)))
        {
            var segments = branch.FriendlyName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) continue;

            var remoteName = segments[0];
            if (!remoteRoot.TryGetValue(remoteName, out var root))
            {
                root = new RemoteTreeNode(remoteName, null);
                remoteRoot[remoteName] = root;
            }

            var current = root;
            for (var i = 1; i < segments.Length - 1; i++)
            {
                current = current.GetOrAddChild(segments[i]);
            }

            current.Branches.Add(branch);
        }

        var remoteChildren = remoteRoot.Values
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Select(node => node.ToGitRefNode(mainRefName, currentRefName))
            .ToList();

        var tagChildren = repo.Tags
            .OrderBy(tag => tag.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(tag => new GitRefNode
            {
                DisplayName = tag.FriendlyName,
                RefName = tag.CanonicalName,
                Kind = GitRefKind.Tag,
                IsSelectable = true,
                IsCurrent = false,
                IsMain = false,
                Children = []
            })
            .ToList();

        return
        [
            headNode,
            new GitRefNode
            {
                DisplayName = "Local",
                RefName = null,
                Kind = GitRefKind.Category,
                IsSelectable = false,
                IsCurrent = false,
                IsMain = false,
                Children = localChildren
            },
            new GitRefNode
            {
                DisplayName = "Remote",
                RefName = null,
                Kind = GitRefKind.Category,
                IsSelectable = false,
                IsCurrent = false,
                IsMain = false,
                Children = remoteChildren
            },
            new GitRefNode
            {
                DisplayName = "Tags",
                RefName = null,
                Kind = GitRefKind.Category,
                IsSelectable = false,
                IsCurrent = false,
                IsMain = false,
                Children = tagChildren
            }
        ];
    }

    private static GitSnapshot GetSnapshotCore(string solutionFilePathOrDirectory, int commitCount)
    {
        var discoveryPath = Directory.Exists(solutionFilePathOrDirectory)
            ? solutionFilePathOrDirectory
            : Path.GetDirectoryName(solutionFilePathOrDirectory) ?? solutionFilePathOrDirectory;
        var gitPath = Repository.Discover(discoveryPath);
        if (gitPath is null) return GitSnapshot.Empty();

        using var repo = new Repository(gitPath);
        var repoRoot = NormalizePath(repo.Info.WorkingDirectory);
        var gitDirectoryPath = NormalizePath(repo.Info.Path);
        var repositoryContext = new GitRepositoryContext
        {
            IsRepositoryDiscovered = true,
            RepoRootPath = repoRoot,
            GitDirectoryPath = gitDirectoryPath,
            BranchDisplayName = repo.Info.IsHeadDetached ? "Detached HEAD" : repo.Head.FriendlyName,
            IsDetachedHead = repo.Info.IsHeadDetached
        };

        var statusOptions = new StatusOptions
        {
            IncludeIgnored = false,
            IncludeUnaltered = false,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            ExcludeSubmodules = true,
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = true
        };
        var workingTreeEntries = repo.RetrieveStatus(statusOptions)
            .Where(entry => entry.State is not FileStatus.Ignored and not FileStatus.Unaltered)
            .Select(entry => MapWorkingTreeEntry(repo, entry))
            .OrderBy(entry => entry.Group)
            .ThenBy(entry => entry.RepoRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var commits = repo.Commits
            .Take(commitCount)
            .Select(commit => new GitCommitSummary
            {
                Sha = commit.Sha,
                ShortSha = commit.Sha[..Math.Min(8, commit.Sha.Length)],
                Subject = commit.MessageShort,
                AuthorName = commit.Author.Name,
                AuthoredAt = commit.Author.When,
                IsMergeCommit = commit.Parents.Skip(1).Any()
            })
            .ToList();
        IReadOnlyList<GitStashEntry> stashes;
        try
        {
            stashes = LoadStashes(repoRoot);
        }
        catch
        {
            stashes = [];
        }

        return new GitSnapshot
        {
            Repository = repositoryContext,
            WorkingTreeEntries = workingTreeEntries,
            RecentCommits = commits,
            Stashes = stashes
        };
    }

    private async Task<List<GitHistoryRow>> LoadGraphHistoryRows(
        Repository repo,
        string? refName,
        bool includeAllRefs,
        int skip,
        int take,
        GitConfiguredIdentity configuredIdentity,
        IReadOnlySet<string>? primaryBranchCommits,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "log",
            "--date-order",
            "--decorate=short",
            $"--skip={skip}",
            $"-n{take}",
            "--format=format:%x1e%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cI%x1f%D%x1f%s"
        };
        AppendHistoryScopeArguments(arguments, refName, includeAllRefs);
        var result = await ExecuteGitBufferedAsync(
            repo.Info.WorkingDirectory,
            arguments,
            cancellationToken);
        EnsureSuccess(result, "Failed to load git history.");

        var rawRows = new List<RawHistoryRow>();
        foreach (var line in NormalizeNewLines(result.StandardOutput).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var recordStart = line.IndexOf('\u001e');
            if (recordStart < 0) continue;

            var payload = line[(recordStart + 1)..];
            var fields = payload.Split('\u001f');
            if (fields.Length < 8) continue;

            rawRows.Add(new RawHistoryRow(
                fields[0],
                string.IsNullOrWhiteSpace(fields[1]) ? [] : fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[2],
                fields[3],
                DateTimeOffset.Parse(fields[4]),
                DateTimeOffset.Parse(fields[5]),
                ParseDecorations(fields[6]),
                fields[7]));
        }

        var laneDefinitions = BuildBranchLaneDefinitions(repo);
        var commitLaneMap = BuildCommitLaneMap(laneDefinitions);
        return BuildGraphRows(rawRows, laneDefinitions, commitLaneMap, configuredIdentity, primaryBranchCommits).ToList();
    }

    private async Task<HashSet<string>> LoadCommitMetadataMatches(
        string repoRoot,
        string? refName,
        bool includeAllRefs,
        string searchTerm,
        int take,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "log",
            "--date-order",
            $"-n{take}",
            "--format=format:%x1e%H%x1f%s%x1f%b"
        };
        AppendHistoryScopeArguments(arguments, refName, includeAllRefs);
        var result = await ExecuteGitBufferedAsync(
            repoRoot,
            arguments,
            cancellationToken);
        EnsureSuccess(result, "Failed to search git history.");

        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in ParseNullSeparatedRecords(result.StandardOutput))
        {
            var sha = record[0];
            var haystack = string.Join('\n', record.Skip(1));
            if (sha.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                haystack.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(sha);
            }
        }

        return matches;
    }

    private async Task<HashSet<string>> LoadPathMatches(
        string repoRoot,
        string? refName,
        bool includeAllRefs,
        string searchTerm,
        int take,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "log",
            "--date-order",
            $"-n{take}",
            "--name-only",
            "--format=format:%x1e%H"
        };
        AppendHistoryScopeArguments(arguments, refName, includeAllRefs);
        var result = await ExecuteGitBufferedAsync(
            repoRoot,
            arguments,
            cancellationToken);
        EnsureSuccess(result, "Failed to search git history paths.");

        var matches = new HashSet<string>(StringComparer.Ordinal);
        string? currentSha = null;
        foreach (var line in NormalizeNewLines(result.StandardOutput).Split('\n'))
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (line[0] == '\u001e')
            {
                currentSha = line[1..];
                continue;
            }

            if (currentSha is null) continue;
            if (line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(currentSha);
            }
        }

        return matches;
    }

    private static IReadOnlyList<string[]> ParseNullSeparatedRecords(string output)
    {
        return NormalizeNewLines(output)
            .Split('\u001e', StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Trim('\n'))
            .Where(record => !string.IsNullOrWhiteSpace(record))
            .Select(record => record.Split('\u001f'))
            .ToList();
    }

    private static IReadOnlyList<GitStashEntry> LoadStashes(string repoRoot)
    {
        var listResult = ExecuteGitBuffered(repoRoot, ["stash", "list", "--format=%gd%x1f%s%x1e"]);
        EnsureSuccess(listResult, "Failed to load stashes.");

        var stashes = new List<GitStashEntry>();
        foreach (var record in ParseNullSeparatedRecords(listResult.StandardOutput))
        {
            if (record.Length is 0 || string.IsNullOrWhiteSpace(record[0])) continue;

            var stashRef = record[0];
            var files = new List<GitStashChangedFile>();
            files.AddRange(LoadStashWorkingTreeFiles(repoRoot, stashRef));
            if (RevisionExists(repoRoot, $"{stashRef}^3"))
            {
                files.AddRange(LoadStashUntrackedFiles(repoRoot, stashRef));
            }

            stashes.Add(new GitStashEntry
            {
                StashRef = stashRef,
                Message = record.Length > 1
                    ? string.Join('\u001f', record.Skip(1))
                    : string.Empty,
                Files = files
            });
        }

        return stashes;
    }

    private static IReadOnlyList<GitStashChangedFile> LoadStashWorkingTreeFiles(string repoRoot, string stashRef)
    {
        var result = ExecuteGitBuffered(repoRoot, ["diff-tree", "--no-commit-id", "--name-status", "-r", "-M", $"{stashRef}^1", stashRef]);
        EnsureSuccess(result, "Failed to load stash files.");

        return ParseNameStatusEntries(result.StandardOutput)
            .Select(file => new GitStashChangedFile
            {
                RepoRelativePath = file.RepoRelativePath,
                OldRepoRelativePath = file.OldRepoRelativePath,
                StatusCode = file.StatusCode,
                DisplayPath = file.DisplayPath,
                ContentKind = GitStashFileContentKind.WorkingTreeSnapshot
            })
            .ToList();
    }

    private static IReadOnlyList<GitStashChangedFile> LoadStashUntrackedFiles(string repoRoot, string stashRef)
    {
        var result = ExecuteGitBuffered(repoRoot, ["diff-tree", "--root", "--no-commit-id", "--name-status", "-r", $"{stashRef}^3"]);
        EnsureSuccess(result, "Failed to load stashed untracked files.");

        return ParseNameStatusEntries(result.StandardOutput)
            .Select(file => new GitStashChangedFile
            {
                RepoRelativePath = file.RepoRelativePath,
                OldRepoRelativePath = file.OldRepoRelativePath,
                StatusCode = file.StatusCode,
                DisplayPath = file.DisplayPath,
                ContentKind = GitStashFileContentKind.UntrackedSnapshot
            })
            .ToList();
    }

    private static bool RevisionExists(string repoRoot, string revision)
    {
        var result = ExecuteGitBuffered(repoRoot, ["rev-parse", "--verify", "--quiet", revision]);
        return result.ExitCode is 0;
    }

    private static IReadOnlyList<NameStatusEntry> ParseNameStatusEntries(string output)
    {
        var files = new List<NameStatusEntry>();
        foreach (var line in NormalizeNewLines(output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;

            var statusCode = parts[0];
            if (statusCode.StartsWith("R", StringComparison.Ordinal))
            {
                if (parts.Length < 3) continue;
                var oldPath = NormalizeRepositoryRelativePath(parts[1]);
                var newPath = NormalizeRepositoryRelativePath(parts[2]);
                files.Add(new NameStatusEntry(newPath, oldPath, "R", $"{oldPath} -> {newPath}"));
                continue;
            }

            var path = NormalizeRepositoryRelativePath(parts[^1]);
            files.Add(new NameStatusEntry(path, null, statusCode[..1], path));
        }

        return files;
    }

    private static IReadOnlyList<string> ParseDecorations(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<BranchLaneDefinition> BuildBranchLaneDefinitions(Repository repo)
    {
        var mainRefName = ResolveMainRefName(repo);
        var orderedBranches = repo.Branches
            .Where(branch => !branch.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
            .OrderBy(branch => string.Equals(branch.CanonicalName, mainRefName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(branch => branch.IsRemote ? 1 : 0)
            .ThenBy(branch => branch.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var definitions = new List<BranchLaneDefinition>(orderedBranches.Count);
        for (var laneIndex = 0; laneIndex < orderedBranches.Count; laneIndex++)
        {
            var branch = orderedBranches[laneIndex];
            definitions.Add(new BranchLaneDefinition(
                branch.CanonicalName,
                laneIndex,
                laneIndex,
                EnumerateFirstParentShas(branch.Tip).ToHashSet(StringComparer.Ordinal)));
        }

        return definitions;
    }

    private static Dictionary<string, int> BuildCommitLaneMap(IReadOnlyList<BranchLaneDefinition> laneDefinitions)
    {
        var laneMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lane in laneDefinitions.OrderBy(definition => definition.LaneIndex))
        {
            foreach (var sha in lane.FirstParentCommits)
            {
                laneMap.TryAdd(sha, lane.LaneIndex);
            }
        }

        return laneMap;
    }

    private static IReadOnlyList<GitHistoryRow> BuildGraphRows(
        IReadOnlyList<RawHistoryRow> rawRows,
        IReadOnlyList<BranchLaneDefinition> laneDefinitions,
        IReadOnlyDictionary<string, int> commitLaneMap,
        GitConfiguredIdentity configuredIdentity,
        IReadOnlySet<string>? primaryBranchCommits)
    {
        var rows = new List<GitHistoryRow>(rawRows.Count);
        var activeLanes = new HashSet<int>();
        foreach (var rawRow in rawRows)
        {
            var commitLane = ResolveLaneIndex(rawRow.Sha, commitLaneMap);
            var parentLanes = rawRow.ParentShas
                .Select(parentSha => ResolveLaneIndex(parentSha, commitLaneMap))
                .Distinct()
                .ToList();

            var segments = new List<GitGraphSegment>();
            foreach (var lane in activeLanes.OrderBy(lane => lane))
            {
                segments.Add(CreateVerticalSegment(lane, GitGraphAnchor.Top, GitGraphAnchor.Center, lane));
            }

            var nextActiveLanes = new HashSet<int>(activeLanes);
            nextActiveLanes.Remove(commitLane);
            foreach (var parentLane in parentLanes)
            {
                nextActiveLanes.Add(parentLane);
            }

            foreach (var lane in nextActiveLanes.OrderBy(lane => lane))
            {
                segments.Add(CreateVerticalSegment(lane, GitGraphAnchor.Center, GitGraphAnchor.Bottom, lane));
            }

            for (var parentIndex = 0; parentIndex < parentLanes.Count; parentIndex++)
            {
                var parentLane = parentLanes[parentIndex];
                if (parentLane == commitLane)
                {
                    continue;
                }

                segments.Add(new GitGraphSegment
                {
                    FromColumnIndex = commitLane,
                    FromAnchor = GitGraphAnchor.Center,
                    ToColumnIndex = parentLane,
                    ToAnchor = GitGraphAnchor.Bottom,
                    ColorIndex = parentIndex is 0 ? commitLane : parentLane
                });
            }

            rows.Add(new GitHistoryRow
            {
                Sha = rawRow.Sha,
                ShortSha = rawRow.Sha[..Math.Min(8, rawRow.Sha.Length)],
                Subject = rawRow.Subject,
                AuthorName = rawRow.AuthorName,
                AuthorEmail = rawRow.AuthorEmail,
                AuthoredAt = rawRow.AuthoredAt,
                CommittedAt = rawRow.CommittedAt,
                FriendlyTimestamp = FormatFriendlyTimestamp(rawRow.AuthoredAt),
                FriendlyCommittedTimestamp = FormatFriendlyTimestamp(rawRow.CommittedAt),
                IsMergeCommit = rawRow.ParentShas.Count > 1,
                IsLocalAuthor = configuredIdentity.Matches(rawRow.AuthorName, rawRow.AuthorEmail),
                IsSelectable = true,
                IsPrimaryBranchCommit = primaryBranchCommits?.Contains(rawRow.Sha) ?? true,
                CommitLaneIndex = commitLane,
                CommitColorIndex = commitLane,
                GraphSegments = segments,
                GraphPrefix = string.Empty,
                GraphCells =
                [
                    new GitGraphCell
                    {
                        Kind = GitGraphCellKind.Commit,
                        ColumnIndex = commitLane,
                        ColorIndex = commitLane
                    }
                ],
                Decorations = rawRow.Decorations
            });

            activeLanes = nextActiveLanes;
        }

        return rows;
    }

    private static GitGraphSegment CreateVerticalSegment(int lane, GitGraphAnchor from, GitGraphAnchor to, int colorIndex)
    {
        return new GitGraphSegment
        {
            FromColumnIndex = lane,
            FromAnchor = from,
            ToColumnIndex = lane,
            ToAnchor = to,
            ColorIndex = colorIndex
        };
    }

    private static int ResolveLaneIndex(string sha, IReadOnlyDictionary<string, int> commitLaneMap)
    {
        return commitLaneMap.TryGetValue(sha, out var laneIndex)
            ? laneIndex
            : 0;
    }

    private static IEnumerable<string> EnumerateFirstParentShas(Commit? tip)
    {
        for (var commit = tip; commit is not null; commit = commit.Parents.FirstOrDefault())
        {
            yield return commit.Sha;
        }
    }

    private static string ResolveMainRefName(Repository repo)
    {
        if (repo.Refs["refs/remotes/origin/HEAD"] is SymbolicReference originHead)
        {
            return originHead.TargetIdentifier;
        }

        if (repo.Branches["main"] is { } main)
        {
            return main.CanonicalName;
        }

        if (repo.Branches["master"] is { } master)
        {
            return master.CanonicalName;
        }

        return repo.Head.CanonicalName;
    }

    private static GitRefNode CreateBranchNode(Branch branch, string mainRefName, string currentRefName)
    {
        var markers = new List<string>();
        if (string.Equals(branch.CanonicalName, currentRefName, StringComparison.Ordinal))
        {
            markers.Add("HEAD");
        }

        if (string.Equals(branch.CanonicalName, mainRefName, StringComparison.Ordinal))
        {
            markers.Add("★");
        }

        var displayName = markers.Count is 0
            ? branch.FriendlyName
            : $"{branch.FriendlyName} {string.Join(' ', markers)}";

        return new GitRefNode
        {
            DisplayName = displayName,
            RefName = branch.CanonicalName,
            Kind = branch.IsRemote ? GitRefKind.RemoteBranch : GitRefKind.LocalBranch,
            IsSelectable = true,
            IsCurrent = string.Equals(branch.CanonicalName, currentRefName, StringComparison.Ordinal),
            IsMain = string.Equals(branch.CanonicalName, mainRefName, StringComparison.Ordinal),
            Children = []
        };
    }

    private static string FormatFriendlyTimestamp(DateTimeOffset timestamp)
    {
        var local = timestamp.LocalDateTime;
        var now = DateTime.Now;
        if (local.Date == now.Date)
        {
            return $"Today {local:h:mm tt}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"Yesterday {local:h:mm tt}";
        }

        return local.ToString("M/d/yy, h:mm tt");
    }

    private static async Task<string> ExecuteGitForText(string repoRoot, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await ExecuteGitBufferedAsync(repoRoot, args, cancellationToken);
        EnsureSuccess(result, "git command failed.");
        return NormalizeNewLines(result.StandardOutput);
    }

    private async Task<string> ReadWorkingTextAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var openText = await _openTabsFileManager.GetFileTextIfOpenAsync(absolutePath);
        if (openText is not null)
        {
            return NormalizeNewLines(openText);
        }

        if (!File.Exists(absolutePath))
        {
            return string.Empty;
        }

        return NormalizeNewLines(await File.ReadAllTextAsync(absolutePath, cancellationToken));
    }

    private static GitWorkingTreeEntry? GetCurrentWorkingTreeEntry(Repository repo, string absolutePath)
    {
        var repoRelativePath = NormalizeRepositoryRelativePath(Path.GetRelativePath(repo.Info.WorkingDirectory, NormalizePath(absolutePath)));
        var statusOptions = new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = true,
            PathSpec = [repoRelativePath],
            IncludeUnaltered = false
        };
        var statusEntry = repo.RetrieveStatus(statusOptions)
            .FirstOrDefault(entry => NormalizeRepositoryRelativePath(entry.FilePath).Equals(repoRelativePath, StringComparison.OrdinalIgnoreCase));
        return statusEntry is null ? null : MapWorkingTreeEntry(repo, statusEntry);
    }

    private async Task<GitFileContentViewModel> GetMergeConflictView(Repository repo, string absolutePath, string repoRelativePath, CancellationToken cancellationToken)
    {
        var localResult = await ExecuteGitBufferedAsync(repo.Info.WorkingDirectory, ["show", $":2:{repoRelativePath}"], cancellationToken);
        var incomingResult = await ExecuteGitBufferedAsync(repo.Info.WorkingDirectory, ["show", $":3:{repoRelativePath}"], cancellationToken);
        var currentText = await ReadWorkingTextAsync(absolutePath, cancellationToken);

        return new GitFileContentViewModel
        {
            Kind = GitFileContentViewKind.MergeConflict,
            AbsolutePath = absolutePath,
            RepoRelativePath = repoRelativePath,
            MergeConflictView = new GitMergeConflictViewModel
            {
                AbsolutePath = absolutePath,
                RepoRelativePath = repoRelativePath,
                LocalText = localResult.ExitCode is 0 ? localResult.StandardOutput : string.Empty,
                CurrentText = currentText,
                IncomingText = incomingResult.ExitCode is 0 ? incomingResult.StandardOutput : string.Empty
            }
        };
    }

    private static async Task<string> GetDiffPatchText(string repoRoot, string repoRelativePath, bool staged, int unifiedLines, CancellationToken cancellationToken)
    {
        return await GetDiffPatchText(repoRoot, repoRelativePath, revisionSpec: null, staged, unifiedLines, cancellationToken);
    }

    private static async Task<string> GetDiffPatchText(string repoRoot, string repoRelativePath, string? revisionSpec, int unifiedLines, CancellationToken cancellationToken)
    {
        return await GetDiffPatchText(repoRoot, repoRelativePath, revisionSpec, staged: false, unifiedLines, cancellationToken);
    }

    private static async Task<string> GetDiffPatchText(string repoRoot, string repoRelativePath, string? revisionSpec, bool staged, int unifiedLines, CancellationToken cancellationToken)
    {
        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(GetDiffPatchText)}");
        activity?.SetTag("git.diff.repo_root", repoRoot);
        activity?.SetTag("git.diff.repo_relative_path", repoRelativePath);
        activity?.SetTag("git.diff.staged", staged);
        activity?.SetTag("git.diff.revision_spec", revisionSpec);
        var args = new List<string> { "diff" };
        if (staged)
        {
            args.Add("--cached");
        }
        else if (string.IsNullOrWhiteSpace(revisionSpec) is false)
        {
            args.Add(revisionSpec);
        }

        args.Add("--no-color");
        args.Add("--no-ext-diff");
        args.Add($"--unified={unifiedLines}");
        args.Add("--");
        args.Add(repoRelativePath);

        var result = await ExecuteGitBufferedAsync(repoRoot, args, cancellationToken);
        return result.ExitCode is 0 ? result.StandardOutput : string.Empty;
    }

    private static async Task<string> GetRevisionFileText(string repoRoot, string repoRelativePath, string revisionSpec, CancellationToken cancellationToken)
    {
        using var activity = SharpIdeOtel.Source.StartActivity($"{nameof(GitService)}.{nameof(GetRevisionFileText)}");
        activity?.SetTag("git.diff.repo_root", repoRoot);
        activity?.SetTag("git.diff.repo_relative_path", repoRelativePath);
        activity?.SetTag("git.diff.revision_spec", revisionSpec);
        var result = await ExecuteGitBufferedAsync(repoRoot, ["show", "--no-color", "--textconv", revisionSpec], cancellationToken);
        return result.ExitCode is 0 ? result.StandardOutput.Replace("\r\n", "\n") : string.Empty;
    }

    private static GitWorkingTreeEntry MapWorkingTreeEntry(Repository repo, StatusEntry entry)
    {
        var absolutePath = NormalizePath(Path.Combine(repo.Info.WorkingDirectory, entry.FilePath));
        var existsInHead = ExistsInHead(repo.Head.Tip, entry.FilePath);
        var isTracked = existsInHead || repo.Index[entry.FilePath] is not null;
        var status = GitWorkingTreeStatus.None;

        if (entry.State.HasFlag(FileStatus.ModifiedInIndex) || entry.State.HasFlag(FileStatus.ModifiedInWorkdir))
        {
            status |= GitWorkingTreeStatus.Modified;
        }

        if (entry.State.HasFlag(FileStatus.DeletedFromIndex) || entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
        {
            status |= GitWorkingTreeStatus.Deleted;
        }

        if (entry.State.HasFlag(FileStatus.RenamedInIndex) || entry.State.HasFlag(FileStatus.RenamedInWorkdir))
        {
            status |= GitWorkingTreeStatus.Renamed;
        }

        if (entry.State.HasFlag(FileStatus.TypeChangeInIndex) || entry.State.HasFlag(FileStatus.TypeChangeInWorkdir))
        {
            status |= GitWorkingTreeStatus.TypeChange;
        }

        if (entry.State.HasFlag(FileStatus.Conflicted))
        {
            status |= GitWorkingTreeStatus.Conflicted;
        }

        if (!isTracked)
        {
            status |= GitWorkingTreeStatus.Unversioned;
        }

        var hasIndexChanges = HasIndexChanges(entry.State);
        var hasWorktreeChanges = HasWorktreeChanges(entry.State);

        return new GitWorkingTreeEntry
        {
            AbsolutePath = absolutePath,
            RepoRelativePath = NormalizeRepositoryRelativePath(entry.FilePath),
            Group = isTracked ? GitWorkingTreeGroup.ChangedFiles : GitWorkingTreeGroup.UnversionedFiles,
            Status = status,
            StageDisplayState = MapStageDisplayState(hasIndexChanges, hasWorktreeChanges),
            IsStaged = hasIndexChanges,
            IsTracked = isTracked
        };
    }

    private static bool ExistsInHead(Commit? headCommit, string repoRelativePath)
    {
        if (headCommit is null) return false;

        Tree tree = headCommit.Tree;
        var segments = NormalizeRepositoryRelativePath(repoRelativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var entry = tree[segments[i]];
            if (entry is null) return false;
            if (i == segments.Length - 1) return entry.TargetType is TreeEntryTargetType.Blob;
            if (entry.TargetType is not TreeEntryTargetType.Tree) return false;
            tree = (Tree)entry.Target;
        }

        return false;
    }

    private static bool HasIndexChanges(FileStatus status)
    {
        return status.HasFlag(FileStatus.NewInIndex)
               || status.HasFlag(FileStatus.ModifiedInIndex)
               || status.HasFlag(FileStatus.DeletedFromIndex)
               || status.HasFlag(FileStatus.RenamedInIndex)
               || status.HasFlag(FileStatus.TypeChangeInIndex);
    }

    private static bool HasWorktreeChanges(FileStatus status)
    {
        return status.HasFlag(FileStatus.NewInWorkdir)
               || status.HasFlag(FileStatus.ModifiedInWorkdir)
               || status.HasFlag(FileStatus.DeletedFromWorkdir)
               || status.HasFlag(FileStatus.RenamedInWorkdir)
               || status.HasFlag(FileStatus.TypeChangeInWorkdir);
    }

    private static GitStageDisplayState MapStageDisplayState(bool hasIndexChanges, bool hasWorktreeChanges)
    {
        if (hasIndexChanges && hasWorktreeChanges)
        {
            return GitStageDisplayState.Partial;
        }

        if (hasIndexChanges)
        {
            return GitStageDisplayState.Staged;
        }

        return GitStageDisplayState.Unstaged;
    }

    private static Signature ResolveSignature(Repository repo)
    {
        var name = repo.Config.Get<string>("user.name")?.Value;
        var email = repo.Config.Get<string>("user.email")?.Value;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Git commit requires configured user.name and user.email.");
        }

        return new Signature(name, email, DateTimeOffset.Now);
    }

    private static Repository OpenRepository(string repoRoot)
    {
        var gitPath = Repository.Discover(repoRoot) ?? throw new InvalidOperationException($"No git repository was found for '{repoRoot}'.");
        return new Repository(gitPath);
    }

    private static Repository OpenRepositoryForPath(string absolutePath)
    {
        return OpenRepository(Path.GetDirectoryName(absolutePath) ?? absolutePath);
    }

    private static IEnumerable<string> ToRepositoryRelativePaths(Repository repo, IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(NormalizePath).Distinct(StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(repo.Info.WorkingDirectory, path);
            yield return NormalizeRepositoryRelativePath(relativePath);
        }
    }

    private static string NormalizeRepositoryRelativePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IReadOnlyList<string> BuildSelectiveStashArguments(IReadOnlyList<string> relativePaths, string message, bool includeUntracked)
    {
        var args = new List<string>
        {
            "stash",
            "push",
            "-m",
            message
        };
        if (includeUntracked)
        {
            args.Add("-u");
        }

        args.Add("--");
        args.AddRange(relativePaths);
        return args;
    }

    private static IReadOnlyList<string> BuildRestoreArguments(string? source, bool staged, bool worktree, IReadOnlyList<string> relativePaths)
    {
        var args = new List<string> { "restore" };
        if (staged)
        {
            args.Add("--staged");
        }

        if (worktree)
        {
            args.Add("--worktree");
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            args.Add("--source");
            args.Add(source);
        }

        args.Add("--");
        args.AddRange(relativePaths);
        return args;
    }

    private static void DeleteWorktreePath(string repoRoot, string repoRelativePath)
    {
        var absolutePath = Path.Combine(repoRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
    }

    private static async Task<BufferedCommandResult> ExecuteGitBufferedAsync(string repoRoot, IEnumerable<string> args, CancellationToken cancellationToken, string? stdinText = null)
    {
        var command = Cli.Wrap("git")
            .WithArguments(argumentBuilder =>
            {
                argumentBuilder.Add("-C");
                argumentBuilder.Add(repoRoot);
                foreach (var arg in args)
                {
                    argumentBuilder.Add(arg);
                }
            })
            .WithValidation(CommandResultValidation.None);

        if (stdinText is not null)
        {
            command = command.WithStandardInputPipe(PipeSource.FromString(stdinText));
        }

        return await command.ExecuteBufferedAsync(cancellationToken);
    }

    private static GitCommandResult ExecuteGitBuffered(string repoRoot, IEnumerable<string> args, string? stdinText = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinText is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repoRoot);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        if (stdinText is not null)
        {
            process.StandardInput.Write(stdinText);
            process.StandardInput.Close();
        }

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new GitCommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n");

    private static void EnsureSuccess(GitCommandResult result, string fallbackMessage)
    {
        if (result.ExitCode is 0) return;

        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? fallbackMessage : error);
    }

    private static void EnsureSuccess(BufferedCommandResult result, string fallbackMessage)
    {
        if (result.ExitCode is 0) return;

        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? fallbackMessage : error);
    }

    private async Task<IReadOnlySet<string>?> LoadPrimaryBranchCommits(Repository repo, string refName, CancellationToken cancellationToken)
    {
        var mainRefName = ResolveMainRefName(repo);
        if (string.Equals(refName, mainRefName, StringComparison.Ordinal))
        {
            return null;
        }

        var mergeBaseResult = await ExecuteGitBufferedAsync(repo.Info.WorkingDirectory, ["merge-base", refName, mainRefName], cancellationToken);
        if (mergeBaseResult.ExitCode is not 0)
        {
            return null;
        }

        var mergeBase = mergeBaseResult.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(mergeBase))
        {
            return null;
        }

        var result = await ExecuteGitBufferedAsync(repo.Info.WorkingDirectory, ["rev-list", "--first-parent", $"{mergeBase}..{refName}"], cancellationToken);
        EnsureSuccess(result, "Failed to load primary branch commits.");
        return NormalizeNewLines(result.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AppendHistoryScopeArguments(ICollection<string> arguments, string? refName, bool includeAllRefs)
    {
        if (includeAllRefs)
        {
            arguments.Add("--branches");
            arguments.Add("--remotes");
            return;
        }

        if (string.IsNullOrWhiteSpace(refName))
        {
            throw new InvalidOperationException("A ref name is required when loading scoped git history.");
        }

        arguments.Add(refName);
    }

    private static GitConfiguredIdentity ResolveConfiguredIdentity(Repository repo)
    {
        return new GitConfiguredIdentity(
            repo.Config.Get<string>("user.name")?.Value,
            repo.Config.Get<string>("user.email")?.Value);
    }

    private sealed class RemoteTreeNode(string name, Branch? branch)
    {
        public string Name { get; } = name;
        public Branch? Branch { get; } = branch;
        public List<Branch> Branches { get; } = [];
        public Dictionary<string, RemoteTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public RemoteTreeNode GetOrAddChild(string segment)
        {
            if (Children.TryGetValue(segment, out var existing))
            {
                return existing;
            }

            var created = new RemoteTreeNode(segment, null);
            Children[segment] = created;
            return created;
        }

        public GitRefNode ToGitRefNode(string mainRefName, string currentRefName)
        {
            var childNodes = new List<GitRefNode>();
            childNodes.AddRange(Children.Values
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => child.ToGitRefNode(mainRefName, currentRefName)));
            childNodes.AddRange(Branches
                .OrderBy(branch => branch.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .Select(branch => CreateBranchNode(branch, mainRefName, currentRefName)));

            return new GitRefNode
            {
                DisplayName = Name,
                RefName = Branch?.CanonicalName,
                Kind = GitRefKind.Category,
                IsSelectable = false,
                IsCurrent = false,
                IsMain = false,
                Children = childNodes
            };
        }
    }

    private readonly record struct GitConfiguredIdentity(string? Name, string? Email)
    {
        public bool Matches(string authorName, string authorEmail)
        {
            if (!string.IsNullOrWhiteSpace(Email))
            {
                return string.Equals(Email, authorEmail, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(Name))
            {
                return string.Equals(Name, authorName, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }

    private sealed record NameStatusEntry(
        string RepoRelativePath,
        string? OldRepoRelativePath,
        string StatusCode,
        string DisplayPath);

    private sealed record RawHistoryRow(
        string Sha,
        IReadOnlyList<string> ParentShas,
        string AuthorName,
        string AuthorEmail,
        DateTimeOffset AuthoredAt,
        DateTimeOffset CommittedAt,
        IReadOnlyList<string> Decorations,
        string Subject);

    private sealed record BranchLaneDefinition(
        string RefName,
        int LaneIndex,
        int ColorIndex,
        IReadOnlySet<string> FirstParentCommits);
}
