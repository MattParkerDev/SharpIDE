using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpIDE.Application.Features.Git;

internal sealed class GitSelectivePatchBuilder
{
    private static readonly Regex HunkHeaderRegex = new(@"^@@ -(?<oldStart>\d+)(,(?<oldCount>\d+))? \+(?<newStart>\d+)(,(?<newCount>\d+))? @@", RegexOptions.Compiled);

    public ParsedDiffFile ParseUnifiedDiff(string patchText, string repoRelativePath)
    {
        var normalizedLines = NormalizeNewLines(patchText).Split('\n', StringSplitOptions.None);
        var fileHeaderLines = new List<string>();
        var hunks = new List<ParsedHunk>();
        string? currentHeader = null;
        HunkRange? currentRange = null;
        List<string>? currentBody = null;

        void FlushCurrent()
        {
            if (currentHeader is null || currentRange is null || currentBody is null)
            {
                return;
            }

            hunks.Add(new ParsedHunk(currentHeader, currentRange.Value, currentBody.ToArray()));
            currentHeader = null;
            currentRange = null;
            currentBody = null;
        }

        foreach (var line in normalizedLines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushCurrent();
                currentHeader = line;
                currentRange = ParseHunkRange(line);
                currentBody = [];
                continue;
            }

            if (currentBody is null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    fileHeaderLines.Add(line);
                }

                continue;
            }

            currentBody.Add(line);
        }

        FlushCurrent();
        return new ParsedDiffFile(repoRelativePath, fileHeaderLines.ToArray(), hunks.ToArray());
    }

    public ParsedDiffFile CreateUntrackedFileDiff(string repoRelativePath, string currentText)
    {
        var lines = SplitLines(NormalizeNewLines(currentText));
        var bodyLines = lines.Select(static line => "+" + line).ToArray();
        var fileHeaderLines = new[]
        {
            $"diff --git a/{repoRelativePath} b/{repoRelativePath}",
            "new file mode 100644",
            "--- /dev/null",
            $"+++ b/{repoRelativePath}"
        };
        var header = $"@@ -0,0 +1,{lines.Count} @@";
        return new ParsedDiffFile(
            repoRelativePath,
            fileHeaderLines,
            [new ParsedHunk(header, new HunkRange(0, 0, 1, lines.Count), bodyLines)]);
    }

    public GitDiffViewModel BuildCanonicalViewModel(
        string repoRelativePath,
        string absolutePath,
        GitDiffMode mode,
        string baseLabel,
        string currentLabel,
        string baseFileText,
        string currentFileText,
        bool canEditCurrent)
    {
        var normalizedBaseFileText = NormalizeNewLines(baseFileText);
        var normalizedCurrentFileText = NormalizeNewLines(currentFileText);
        var baseLines = SplitLines(normalizedBaseFileText);
        var currentLines = SplitLines(normalizedCurrentFileText);
        var rows = BuildDirectDiffRows(repoRelativePath, baseLines, currentLines)
            .Select((row, index) => (GitDiffDisplayRow)(row with { DisplayIndex = index + 1 }))
            .ToArray();
        var chunks = BuildCanonicalChunks(repoRelativePath, rows);

        return new GitDiffViewModel
        {
            AbsolutePath = absolutePath,
            RepoRelativePath = repoRelativePath,
            Mode = mode,
            BaseLabel = baseLabel,
            CurrentLabel = currentLabel,
            BaseDisplayText = normalizedBaseFileText,
            CurrentDisplayText = normalizedCurrentFileText,
            CanEditCurrent = canEditCurrent,
            CanStageLines = false,
            CanUnstageLines = false,
            CanStageChunks = false,
            CanUnstageChunks = false,
            CanRevertChunks = false,
            Rows = rows,
            Chunks = chunks
        };
    }

    public GitDiffViewModel BuildViewModel(
        ParsedDiffFile parsedDiff,
        string absolutePath,
        GitDiffMode mode,
        string baseLabel,
        string currentLabel,
        string baseFileText,
        string currentFileText,
        bool canEditCurrent)
    {
        return BuildCanonicalViewModel(
            parsedDiff.RepoRelativePath,
            absolutePath,
            mode,
            baseLabel,
            currentLabel,
            baseFileText,
            currentFileText,
            canEditCurrent);
    }

    public RowStateProjection BuildRowStateProjection(
        GitDiffViewModel canonicalView,
        string baseText,
        string indexText,
        string workingText,
        GitDiffActionModel? unstagedActions = null,
        GitDiffActionModel? stagedActions = null)
    {
        var headIndexRows = BuildSnapshotAlignmentRows(
            SplitLines(NormalizeNewLines(baseText)),
            SplitLines(NormalizeNewLines(indexText)),
            SnapshotSide.Head,
            SnapshotSide.Index);
        var indexWorktreeRows = BuildSnapshotAlignmentRows(
            SplitLines(NormalizeNewLines(indexText)),
            SplitLines(NormalizeNewLines(workingText)),
            SnapshotSide.Index,
            SnapshotSide.Worktree);
        var mergedAlignment = MergeAlignmentRows(headIndexRows, indexWorktreeRows);

        var changedRows = canonicalView.Rows
            .Where(static row => row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None)
            .ToArray();
        var chunkOrdinalsByRowId = changedRows
            .GroupBy(static row => row.ChunkId, StringComparer.Ordinal)
            .SelectMany(group => group.Select((row, index) => (row.RowId, index)))
            .ToDictionary(static pair => pair.RowId, static pair => pair.index, StringComparer.Ordinal);

        var rowStates = new Dictionary<string, GitDiffRowState>(StringComparer.Ordinal);
        var mismatches = new List<string>();

        foreach (var row in canonicalView.Rows)
        {
            var state = ResolveRowState(row, mergedAlignment, chunkOrdinalsByRowId, out var matched);
            if (matched)
            {
                rowStates[row.RowId] = new GitDiffRowState
                {
                    RowId = row.RowId,
                    StageState = state
                };
            }
            else if (row.ChunkBackgroundKind is not GitDiffChunkBackgroundKind.None)
            {
                mismatches.Add(row.RowId);
            }
        }

        if (unstagedActions is not null || stagedActions is not null)
        {
            rowStates = MergeActionAnchoredRowStates(canonicalView, rowStates, unstagedActions, stagedActions);
        }

        return new RowStateProjection(rowStates, mismatches.ToArray());
    }

    private static Dictionary<string, GitDiffRowState> MergeActionAnchoredRowStates(
        GitDiffViewModel canonicalView,
        IReadOnlyDictionary<string, GitDiffRowState> fallbackRowStates,
        GitDiffActionModel? unstagedActions,
        GitDiffActionModel? stagedActions)
    {
        var actionStatesByRowId = new Dictionary<string, GitDiffRowStageState>(StringComparer.Ordinal);

        ApplyActionStates(unstagedActions?.LineActions, GitDiffRowStageState.Unstaged, actionStatesByRowId);
        ApplyActionStates(stagedActions?.LineActions, GitDiffRowStageState.Staged, actionStatesByRowId);

        var merged = new Dictionary<string, GitDiffRowState>(StringComparer.Ordinal);
        foreach (var row in canonicalView.Rows)
        {
            if (row.ChunkBackgroundKind is GitDiffChunkBackgroundKind.None)
            {
                if (fallbackRowStates.TryGetValue(row.RowId, out var contextState))
                {
                    merged[row.RowId] = contextState;
                }

                continue;
            }

            var state = actionStatesByRowId.TryGetValue(row.RowId, out var actionState)
                ? actionState
                : fallbackRowStates.TryGetValue(row.RowId, out var fallbackState)
                    ? fallbackState.StageState
                    : GitDiffRowStageState.None;

            merged[row.RowId] = new GitDiffRowState
            {
                RowId = row.RowId,
                StageState = state
            };
        }

        return merged;
    }

    private static void ApplyActionStates(
        IReadOnlyList<GitDiffLineActionAnchor>? actions,
        GitDiffRowStageState state,
        IDictionary<string, GitDiffRowStageState> statesByRowId)
    {
        if (actions is null)
        {
            return;
        }

        foreach (var action in actions)
        {
            if (!statesByRowId.TryGetValue(action.CanonicalRowId, out var currentState))
            {
                statesByRowId[action.CanonicalRowId] = state;
                continue;
            }

            statesByRowId[action.CanonicalRowId] = CombineRowStates(currentState, state);
        }
    }

    private static GitDiffRowStageState CombineRowStates(GitDiffRowStageState current, GitDiffRowStageState update)
    {
        if (current == update)
        {
            return current;
        }

        if (current is GitDiffRowStageState.None)
        {
            return update;
        }

        if (update is GitDiffRowStageState.None)
        {
            return current;
        }

        return GitDiffRowStageState.Mixed;
    }

    public GitDiffActionModel BuildActionModel(
        ParsedDiffFile parsedDiff,
        GitDiffViewModel canonicalView,
        GitPatchOperationMode lineOperationMode,
        IReadOnlyList<GitPatchOperationMode> chunkOperationModes,
        ActionAnchorSide anchorSide)
    {
        var lineActions = new List<GitDiffLineActionAnchor>();
        var canonicalRows = canonicalView.Rows.OrderBy(static row => row.DisplayIndex).ToArray();
        var canonicalRowsById = canonicalRows.ToDictionary(static row => row.RowId, StringComparer.Ordinal);
        var chunkBuildersById = new Dictionary<string, ChunkActionBuilder>(StringComparer.Ordinal);

        foreach (var hunk in parsedDiff.Hunks)
        {
            var atomicChanges = BuildAtomicChanges(hunk);
            var anchoredRows = atomicChanges
                .Select((change, index) => new
                {
                    Change = change,
                    CanonicalRowId = FindCanonicalRowIdForChange(canonicalRows, change, anchorSide, index)
                })
                .Where(static item => item.CanonicalRowId is not null)
                .ToArray();

            foreach (var anchored in anchoredRows)
            {
                lineActions.Add(new GitDiffLineActionAnchor
                {
                    LineActionId = anchored.Change.LineActionId,
                    PatchText = BuildPatchText(parsedDiff.FileHeaderLines, anchored.Change.Header, anchored.Change.BodyLines),
                    OperationMode = lineOperationMode,
                    CanonicalRowId = anchored.CanonicalRowId!
                });
                if (!canonicalRowsById.TryGetValue(anchored.CanonicalRowId!, out var canonicalRow) ||
                    string.IsNullOrEmpty(canonicalRow.ChunkId))
                {
                    continue;
                }

                if (!chunkBuildersById.TryGetValue(canonicalRow.ChunkId, out var chunkBuilder))
                {
                    chunkBuilder = new ChunkActionBuilder(canonicalRow.ChunkId);
                    chunkBuildersById[canonicalRow.ChunkId] = chunkBuilder;
                }

                chunkBuilder.Add(canonicalRow, anchored.Change.LineActionId);
            }
        }

        var chunkActions = new List<GitDiffChunkActionAnchor>();
        foreach (var chunkBuilder in chunkBuildersById.Values.OrderBy(static builder => builder.FirstDisplayIndex))
        {
            var patchText = BuildLinePatch(parsedDiff, chunkBuilder.LineActionIds, lineOperationMode);
            if (string.IsNullOrWhiteSpace(patchText))
            {
                continue;
            }

            foreach (var operationMode in chunkOperationModes)
            {
                chunkActions.Add(new GitDiffChunkActionAnchor
                {
                    ActionChunkId = chunkBuilder.ChunkId,
                    PatchText = patchText,
                    OperationMode = operationMode,
                    FirstCanonicalRowId = chunkBuilder.FirstCanonicalRowId,
                    LastCanonicalRowId = chunkBuilder.LastCanonicalRowId,
                    AnchorCanonicalRowId = chunkBuilder.FirstCanonicalRowId
                });
            }
        }

        return new GitDiffActionModel
        {
            LineActions = lineActions,
            ChunkActions = chunkActions
        };
    }

    public string BuildLinePatch(ParsedDiffFile parsedDiff, IReadOnlyList<string> lineActionIds, GitPatchOperationMode mode)
    {
        if (lineActionIds.Count is 0)
        {
            return string.Empty;
        }

        var selected = new HashSet<string>(lineActionIds, StringComparer.Ordinal);
        var selectedHunks = new List<(string Header, IReadOnlyList<string> BodyLines)>();

        foreach (var hunk in parsedDiff.Hunks)
        {
            foreach (var change in BuildAtomicChanges(hunk))
            {
                if (!selected.Remove(change.LineActionId))
                {
                    continue;
                }

                selectedHunks.Add((change.Header, change.BodyLines));
            }
        }

        if (selectedHunks.Count is 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in parsedDiff.FileHeaderLines)
        {
            builder.AppendLine(line);
        }

        foreach (var (header, bodyLines) in selectedHunks)
        {
            builder.AppendLine(header);
            foreach (var bodyLine in bodyLines)
            {
                builder.AppendLine(bodyLine);
            }
        }

        return builder.ToString();
    }

    public string BuildUntrackedLinePatch(string repoRelativePath, string currentText, IReadOnlyList<string> lineActionIds)
    {
        var selected = new HashSet<string>(lineActionIds, StringComparer.Ordinal);
        var lines = SplitLines(NormalizeNewLines(currentText));
        var selectedLines = new List<string>();

        for (var index = 0; index < lines.Count; index++)
        {
            var lineId = BuildLineActionId(0, index + 1, null, lines[index]);
            if (selected.Contains(lineId))
            {
                selectedLines.Add(lines[index]);
            }
        }

        if (selectedLines.Count is 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"diff --git a/{repoRelativePath} b/{repoRelativePath}");
        builder.AppendLine("new file mode 100644");
        builder.AppendLine("--- /dev/null");
        builder.AppendLine($"+++ b/{repoRelativePath}");
        builder.AppendLine($"@@ -0,0 +1,{selectedLines.Count} @@");
        foreach (var line in selectedLines)
        {
            builder.Append('+');
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    public IReadOnlyList<AtomicDiffChange> BuildAtomicChanges(ParsedHunk hunk)
    {
        var changes = new List<AtomicDiffChange>();
        var removedBuffer = new List<DiffLineEntry>();
        var addedBuffer = new List<DiffLineEntry>();
        var oldLine = hunk.Range.OldStart;
        var newLine = hunk.Range.NewStart;
        int? blockOldStart = null;
        int? blockNewStart = null;

        void Flush()
        {
            if (removedBuffer.Count is 0 && addedBuffer.Count is 0)
            {
                return;
            }

            var pairCount = Math.Max(removedBuffer.Count, addedBuffer.Count);
            for (var i = 0; i < pairCount; i++)
            {
                var removed = i < removedBuffer.Count ? removedBuffer[i] : null;
                var added = i < addedBuffer.Count ? addedBuffer[i] : null;
                var oldStart = removed?.LineNumber ?? blockOldStart ?? oldLine;
                var newStart = added?.LineNumber ?? blockNewStart ?? newLine;
                var header = $"@@ -{oldStart},{(removed is null ? 0 : 1)} +{newStart},{(added is null ? 0 : 1)} @@";
                var bodyLines = new List<string>();
                if (removed is not null)
                {
                    bodyLines.Add("-" + removed.Text);
                }

                if (added is not null)
                {
                    bodyLines.Add("+" + added.Text);
                }

                changes.Add(new AtomicDiffChange(
                    BuildLineActionId(oldStart, newStart, removed?.Text, added?.Text),
                    header,
                    bodyLines.ToArray(),
                    removed?.LineNumber,
                    added?.LineNumber,
                    removed?.Text,
                    added?.Text));
            }

            removedBuffer.Clear();
            addedBuffer.Clear();
            blockOldStart = null;
            blockNewStart = null;
        }

        foreach (var line in hunk.BodyLines)
        {
            if (line.StartsWith("\\", StringComparison.Ordinal))
            {
                continue;
            }

            switch (line)
            {
                case [' ', ..]:
                    Flush();
                    oldLine++;
                    newLine++;
                    break;
                case ['-', '-', '-', ..]:
                case ['+', '+', '+', ..]:
                    break;
                case ['-', ..]:
                    blockOldStart ??= oldLine;
                    blockNewStart ??= newLine;
                    removedBuffer.Add(new DiffLineEntry(line[1..], oldLine));
                    oldLine++;
                    break;
                case ['+', ..]:
                    blockOldStart ??= oldLine;
                    blockNewStart ??= newLine;
                    addedBuffer.Add(new DiffLineEntry(line[1..], newLine));
                    newLine++;
                    break;
            }
        }

        Flush();
        return changes.ToArray();
    }

    internal static string BuildLineActionId(int oldStart, int newStart, string? removedLine, string? addedLine)
    {
        var fingerprintBuilder = new StringBuilder();
        fingerprintBuilder.Append(oldStart);
        fingerprintBuilder.Append(':');
        fingerprintBuilder.Append(newStart);
        fingerprintBuilder.Append('|');
        if (removedLine is not null)
        {
            fingerprintBuilder.Append('-');
            fingerprintBuilder.Append(removedLine);
        }

        fingerprintBuilder.Append('|');
        if (addedLine is not null)
        {
            fingerprintBuilder.Append('+');
            fingerprintBuilder.Append(addedLine);
        }

        return BuildStableId(fingerprintBuilder.ToString());
    }

    internal static string BuildStableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16];
    }

    internal static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n");

    internal static List<string> SplitLines(string normalizedText)
    {
        if (normalizedText.Length is 0)
        {
            return [];
        }

        var lines = normalizedText.Split('\n', StringSplitOptions.None).ToList();
        if (normalizedText.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static GitDiffRowStageState ResolveRowState(
        GitDiffDisplayRow row,
        IReadOnlyList<MergedAlignmentRow> mergedAlignment,
        IReadOnlyDictionary<string, int> chunkOrdinalsByRowId,
        out bool matched)
    {
        matched = false;
        if (row.ChunkBackgroundKind is GitDiffChunkBackgroundKind.None)
        {
            matched = true;
            return GitDiffRowStageState.None;
        }

        var exactLineMatches = mergedAlignment
            .Where(candidate => RowMatchesExactLines(row, candidate) && RowMatchesTexts(row, candidate))
            .ToArray();
        if (exactLineMatches.Length > 0)
        {
            matched = true;
            return exactLineMatches[0].StageState;
        }

        var exactLeftMatches = mergedAlignment
            .Where(candidate => RowMatchesLeftLineAndText(row, candidate))
            .ToArray();
        if (exactLeftMatches.Length is 1)
        {
            matched = true;
            return exactLeftMatches[0].StageState;
        }

        var exactRightMatches = mergedAlignment
            .Where(candidate => RowMatchesRightLineAndText(row, candidate))
            .ToArray();
        if (exactRightMatches.Length is 1)
        {
            matched = true;
            return exactRightMatches[0].StageState;
        }

        var chunkOrdinal = chunkOrdinalsByRowId.GetValueOrDefault(row.RowId, -1);
        var ordinalMatches = mergedAlignment
            .Where(candidate => RowMatchesTexts(row, candidate) && candidate.ChunkLocalOrdinal == chunkOrdinal)
            .ToArray();
        if (ordinalMatches.Length is 1)
        {
            matched = true;
            return ordinalMatches[0].StageState;
        }

        return GitDiffRowStageState.None;
    }

    private static bool RowMatchesExactLines(GitDiffDisplayRow row, MergedAlignmentRow candidate)
    {
        return row.LeftFileLineNumber == candidate.HeadLineNumber &&
               row.RightFileLineNumber == candidate.WorktreeLineNumber;
    }

    private static bool RowMatchesLeftLineAndText(GitDiffDisplayRow row, MergedAlignmentRow candidate)
    {
        return row.LeftFileLineNumber == candidate.HeadLineNumber &&
               TextMatches(row.LeftFileLineNumber, row.LeftText, candidate.HeadLineNumber, candidate.HeadText) &&
               RightPresenceMatches(row, candidate);
    }

    private static bool RowMatchesRightLineAndText(GitDiffDisplayRow row, MergedAlignmentRow candidate)
    {
        return row.RightFileLineNumber == candidate.WorktreeLineNumber &&
               TextMatches(row.RightFileLineNumber, row.RightText, candidate.WorktreeLineNumber, candidate.WorktreeText) &&
               LeftPresenceMatches(row, candidate);
    }

    private static bool RowMatchesTexts(GitDiffDisplayRow row, MergedAlignmentRow candidate)
    {
        return TextMatches(row.LeftFileLineNumber, row.LeftText, candidate.HeadLineNumber, candidate.HeadText) &&
               TextMatches(row.RightFileLineNumber, row.RightText, candidate.WorktreeLineNumber, candidate.WorktreeText);
    }

    private static bool LeftPresenceMatches(GitDiffDisplayRow row, MergedAlignmentRow candidate)
    {
        return row.LeftFileLineNumber.HasValue == candidate.HeadLineNumber.HasValue;
    }

    private static bool RightPresenceMatches(GitDiffDisplayRow row, MergedAlignmentRow candidate)
    {
        return row.RightFileLineNumber.HasValue == candidate.WorktreeLineNumber.HasValue;
    }

    private static bool TextMatches(int? rowLineNumber, string rowText, int? candidateLineNumber, string? candidateText)
    {
        if (rowLineNumber.HasValue != candidateLineNumber.HasValue)
        {
            return false;
        }

        return !rowLineNumber.HasValue || string.Equals(rowText, candidateText, StringComparison.Ordinal);
    }

    private static IReadOnlyList<MergedAlignmentRow> MergeAlignmentRows(
        IReadOnlyList<SnapshotAlignmentRow> headIndexRows,
        IReadOnlyList<SnapshotAlignmentRow> indexWorktreeRows)
    {
        var byIndexKey = new Dictionary<int, MergedAlignmentRowBuilder>();
        var byAnchorKey = new Dictionary<(int AnchorBeforeIndexLine, int InsertionOrdinalAtAnchor), MergedAlignmentRowBuilder>();

        foreach (var row in headIndexRows)
        {
            GetOrCreateBuilder(row, byIndexKey, byAnchorKey).Apply(row);
        }

        foreach (var row in indexWorktreeRows)
        {
            GetOrCreateBuilder(row, byIndexKey, byAnchorKey).Apply(row);
        }

        var ordered = byIndexKey.Values
            .Concat(byAnchorKey.Values)
            .OrderBy(static builder => builder.SortAnchor)
            .ThenBy(static builder => builder.SortKind)
            .ThenBy(static builder => builder.SortOrdinal)
            .Select((builder, index) => builder.Build(index))
            .ToArray();

        return ordered
            .GroupBy(static row => row.CanonicalChunkKey, StringComparer.Ordinal)
            .SelectMany(group => group.Select((row, index) => row with { ChunkLocalOrdinal = index }))
            .ToArray();
    }

    private static MergedAlignmentRowBuilder GetOrCreateBuilder(
        SnapshotAlignmentRow row,
        IDictionary<int, MergedAlignmentRowBuilder> byIndexKey,
        IDictionary<(int AnchorBeforeIndexLine, int InsertionOrdinalAtAnchor), MergedAlignmentRowBuilder> byAnchorKey)
    {
        if (row.IndexLineNumber.HasValue)
        {
            if (!byIndexKey.TryGetValue(row.IndexLineNumber.Value, out var byIndexBuilder))
            {
                byIndexBuilder = new MergedAlignmentRowBuilder(row.AnchorBeforeIndexLine, sortKind: 1, row.InsertionOrdinalAtAnchor);
                byIndexKey[row.IndexLineNumber.Value] = byIndexBuilder;
            }

            return byIndexBuilder;
        }

        var anchorKey = (row.AnchorBeforeIndexLine, row.InsertionOrdinalAtAnchor);
        if (!byAnchorKey.TryGetValue(anchorKey, out var byAnchorBuilder))
        {
            byAnchorBuilder = new MergedAlignmentRowBuilder(row.AnchorBeforeIndexLine, sortKind: 0, row.InsertionOrdinalAtAnchor);
            byAnchorKey[anchorKey] = byAnchorBuilder;
        }

        return byAnchorBuilder;
    }

    private static IReadOnlyList<SnapshotAlignmentRow> BuildSnapshotAlignmentRows(
        IReadOnlyList<string> leftLines,
        IReadOnlyList<string> rightLines,
        SnapshotSide leftSide,
        SnapshotSide rightSide)
    {
        var rows = BuildDirectDiffRows(string.Empty, leftLines, rightLines);
        var alignmentRows = new List<SnapshotAlignmentRow>(rows.Length);
        var lastIndexLine = 0;
        var currentAnchor = int.MinValue;
        var insertionOrdinal = 0;

        foreach (var row in rows)
        {
            var indexLineNumber = GetLineNumber(row, SnapshotSide.Index, leftSide, rightSide);
            if (indexLineNumber.HasValue)
            {
                lastIndexLine = indexLineNumber.Value;
                currentAnchor = int.MinValue;
                insertionOrdinal = 0;
            }
            else
            {
                if (currentAnchor != lastIndexLine)
                {
                    currentAnchor = lastIndexLine;
                    insertionOrdinal = 0;
                }
                else
                {
                    insertionOrdinal++;
                }
            }

            alignmentRows.Add(new SnapshotAlignmentRow
            {
                HeadLineNumber = GetLineNumber(row, SnapshotSide.Head, leftSide, rightSide),
                IndexLineNumber = indexLineNumber,
                WorktreeLineNumber = GetLineNumber(row, SnapshotSide.Worktree, leftSide, rightSide),
                HeadText = GetText(row, SnapshotSide.Head, leftSide, rightSide),
                IndexText = GetText(row, SnapshotSide.Index, leftSide, rightSide),
                WorktreeText = GetText(row, SnapshotSide.Worktree, leftSide, rightSide),
                AnchorBeforeIndexLine = indexLineNumber.HasValue ? indexLineNumber.Value - 1 : lastIndexLine,
                InsertionOrdinalAtAnchor = indexLineNumber.HasValue ? 0 : insertionOrdinal
            });
        }

        return alignmentRows;
    }

    private static int? GetLineNumber(DirectDiffRow row, SnapshotSide snapshotSide, SnapshotSide leftSide, SnapshotSide rightSide)
    {
        if (leftSide == snapshotSide)
        {
            return row.LeftFileLineNumber;
        }

        return rightSide == snapshotSide ? row.RightFileLineNumber : null;
    }

    private static string? GetText(DirectDiffRow row, SnapshotSide snapshotSide, SnapshotSide leftSide, SnapshotSide rightSide)
    {
        if (leftSide == snapshotSide)
        {
            return row.LeftFileLineNumber.HasValue ? row.LeftText : null;
        }

        return rightSide == snapshotSide && row.RightFileLineNumber.HasValue ? row.RightText : null;
    }

    private static string? FindCanonicalRowIdForChange(
        IReadOnlyList<GitDiffDisplayRow> canonicalRows,
        AtomicDiffChange change,
        ActionAnchorSide anchorSide,
        int localHunkOrder)
    {
        var candidates = anchorSide switch
        {
            ActionAnchorSide.Head => FindHeadAnchoredRows(canonicalRows, change),
            ActionAnchorSide.Worktree => FindWorktreeAnchoredRows(canonicalRows, change),
            _ => []
        };

        return candidates
            .OrderBy(candidate => candidate.Row.DisplayIndex)
            .Select((candidate, candidateIndex) => new
            {
                candidate.Row.RowId,
                candidate.Score,
                LocalOrderDistance = Math.Abs(candidateIndex - localHunkOrder),
                candidate.Row.DisplayIndex
            })
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LocalOrderDistance)
            .ThenBy(candidate => candidate.DisplayIndex)
            .Select(candidate => candidate.RowId)
            .FirstOrDefault();
    }

    private static IReadOnlyList<CandidateRow> FindHeadAnchoredRows(IReadOnlyList<GitDiffDisplayRow> canonicalRows, AtomicDiffChange change)
    {
        var matches = new List<CandidateRow>();

        if (change.OldLineNumber.HasValue)
        {
            foreach (var row in canonicalRows.Where(row => row.LeftFileLineNumber == change.OldLineNumber))
            {
                if (change.RemovedText is not null && !string.Equals(row.LeftText, change.RemovedText, StringComparison.Ordinal))
                {
                    continue;
                }

                var score = 0;
                if (change.AddedText is not null && string.Equals(row.RightText, change.AddedText, StringComparison.Ordinal))
                {
                    score -= 10;
                }

                matches.Add(new CandidateRow(row, score));
            }
        }
        else if (change.AddedText is not null)
        {
            foreach (var row in canonicalRows.Where(row =>
                         !row.LeftFileLineNumber.HasValue &&
                         row.RightFileLineNumber == change.NewLineNumber &&
                         string.Equals(row.RightText, change.AddedText, StringComparison.Ordinal)))
            {
                matches.Add(new CandidateRow(row, 0));
            }
        }

        return matches;
    }

    private static IReadOnlyList<CandidateRow> FindWorktreeAnchoredRows(IReadOnlyList<GitDiffDisplayRow> canonicalRows, AtomicDiffChange change)
    {
        var matches = new List<CandidateRow>();

        if (change.NewLineNumber.HasValue)
        {
            foreach (var row in canonicalRows.Where(row => row.RightFileLineNumber == change.NewLineNumber))
            {
                if (change.AddedText is not null && !string.Equals(row.RightText, change.AddedText, StringComparison.Ordinal))
                {
                    continue;
                }

                var score = 0;
                if (change.RemovedText is not null && string.Equals(row.LeftText, change.RemovedText, StringComparison.Ordinal))
                {
                    score -= 10;
                }

                matches.Add(new CandidateRow(row, score));
            }
        }
        else if (change.RemovedText is not null)
        {
            foreach (var row in canonicalRows.Where(row =>
                         !row.RightFileLineNumber.HasValue &&
                         string.Equals(row.LeftText, change.RemovedText, StringComparison.Ordinal)))
            {
                matches.Add(new CandidateRow(row, 0));
            }
        }

        return matches;
    }

    private static IReadOnlyList<GitDiffChunk> BuildCanonicalChunks(string repoRelativePath, IReadOnlyList<GitDiffDisplayRow> rows)
    {
        var chunks = new List<GitDiffChunk>();
        GitDiffDisplayRow? firstRow = null;
        GitDiffDisplayRow? lastRow = null;
        string? currentChunkId = null;
        GitDiffChunkBackgroundKind currentBackground = GitDiffChunkBackgroundKind.None;

        void Flush()
        {
            if (firstRow is null || lastRow is null || currentChunkId is null)
            {
                return;
            }

            chunks.Add(new GitDiffChunk
            {
                ChunkId = currentChunkId,
                FirstDisplayRow = firstRow.DisplayIndex,
                LastDisplayRow = lastRow.DisplayIndex,
                FirstRowId = firstRow.RowId,
                LastRowId = lastRow.RowId,
                BackgroundKind = currentBackground
            });

            firstRow = null;
            lastRow = null;
            currentChunkId = null;
            currentBackground = GitDiffChunkBackgroundKind.None;
        }

        foreach (var row in rows)
        {
            if (row.ChunkBackgroundKind is GitDiffChunkBackgroundKind.None)
            {
                Flush();
                continue;
            }

            if (firstRow is null)
            {
                firstRow = row;
                currentChunkId = row.ChunkId;
                currentBackground = row.ChunkBackgroundKind;
            }
            else if (!string.Equals(currentChunkId, row.ChunkId, StringComparison.Ordinal))
            {
                Flush();
                firstRow = row;
                currentChunkId = row.ChunkId;
                currentBackground = row.ChunkBackgroundKind;
            }

            lastRow = row;
        }

        Flush();
        return chunks;
    }

    private static DirectDiffRow[] BuildDirectDiffRows(string repoRelativePath, IReadOnlyList<string> leftLines, IReadOnlyList<string> rightLines)
    {
        var operations = BuildLineOperations(leftLines, rightLines);
        var rows = new List<DirectDiffRow>();
        var removedBuffer = new List<DiffLineEntry>();
        var addedBuffer = new List<DiffLineEntry>();
        var leftLineNumber = 1;
        var rightLineNumber = 1;
        var changedBlockIndex = 0;

        void FlushChangedBlock()
        {
            if (removedBuffer.Count is 0 && addedBuffer.Count is 0)
            {
                return;
            }

            changedBlockIndex++;
            var pairCount = Math.Max(removedBuffer.Count, addedBuffer.Count);
            var currentSegmentRows = new List<DirectDiffRow>();
            GitDiffChunkBackgroundKind? currentSegmentKind = null;
            var segmentIndex = 0;

            void FlushSegment()
            {
                if (currentSegmentRows.Count is 0 || !currentSegmentKind.HasValue)
                {
                    return;
                }

                segmentIndex++;
                var chunkId = BuildChangedChunkId(repoRelativePath, changedBlockIndex, segmentIndex, currentSegmentKind.Value, currentSegmentRows);
                rows.AddRange(currentSegmentRows.Select(row => row with
                {
                    ChunkId = chunkId,
                    ChunkBackgroundKind = currentSegmentKind.Value
                }));
                currentSegmentRows.Clear();
                currentSegmentKind = null;
            }

            for (var index = 0; index < pairCount; index++)
            {
                var removed = index < removedBuffer.Count ? removedBuffer[index] : null;
                var added = index < addedBuffer.Count ? addedBuffer[index] : null;
                var inlineHighlights = removed is not null && added is not null
                    ? BuildInlineHighlights(removed.Text, added.Text)
                    : default((IReadOnlyList<GitInlineHighlightSpan> Left, IReadOnlyList<GitInlineHighlightSpan> Right)?);
                var kind = removed is not null && added is not null
                    ? GitDiffDisplayRowKind.ModifiedRight
                    : removed is not null
                        ? GitDiffDisplayRowKind.Removed
                        : GitDiffDisplayRowKind.Added;
                var backgroundKind = kind switch
                {
                    GitDiffDisplayRowKind.ModifiedRight => GitDiffChunkBackgroundKind.Modified,
                    GitDiffDisplayRowKind.Removed => GitDiffChunkBackgroundKind.Removed,
                    GitDiffDisplayRowKind.Added => GitDiffChunkBackgroundKind.Added,
                    _ => GitDiffChunkBackgroundKind.None
                };

                if (currentSegmentKind.HasValue && currentSegmentKind.Value != backgroundKind)
                {
                    FlushSegment();
                }

                currentSegmentKind ??= backgroundKind;
                currentSegmentRows.Add(new DirectDiffRow(
                    RowId: BuildStableId($"{repoRelativePath}:row:{removed?.LineNumber ?? 0}:{added?.LineNumber ?? 0}:{removed?.Text}:{added?.Text}:{kind}"),
                    DisplayIndex: 0,
                    ChunkId: string.Empty,
                    Kind: kind,
                    LeftText: removed?.Text ?? string.Empty,
                    RightText: added?.Text ?? string.Empty,
                    LeftFileLineNumber: removed?.LineNumber,
                    RightFileLineNumber: added?.LineNumber,
                    IsSyntheticLeft: removed is null,
                    IsSyntheticRight: added is null,
                    IsEditableRight: added is not null,
                    ChunkBackgroundKind: backgroundKind,
                    InlineHighlightsLeft: inlineHighlights?.Left ?? [],
                    InlineHighlightsRight: inlineHighlights?.Right ?? []));
            }

            FlushSegment();
            removedBuffer.Clear();
            addedBuffer.Clear();
        }

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case DiffOperationKind.Equal:
                    FlushChangedBlock();
                    rows.Add(new DirectDiffRow(
                        RowId: BuildStableId($"{repoRelativePath}:ctx:{leftLineNumber}:{rightLineNumber}:{leftLines[leftLineNumber - 1]}"),
                        DisplayIndex: 0,
                        ChunkId: string.Empty,
                        Kind: GitDiffDisplayRowKind.Context,
                        LeftText: leftLines[leftLineNumber - 1],
                        RightText: rightLines[rightLineNumber - 1],
                        LeftFileLineNumber: leftLineNumber,
                        RightFileLineNumber: rightLineNumber,
                        IsSyntheticLeft: false,
                        IsSyntheticRight: false,
                        IsEditableRight: true,
                        ChunkBackgroundKind: GitDiffChunkBackgroundKind.None,
                        InlineHighlightsLeft: [],
                        InlineHighlightsRight: []));
                    leftLineNumber++;
                    rightLineNumber++;
                    break;
                case DiffOperationKind.Delete:
                    removedBuffer.Add(new DiffLineEntry(leftLines[leftLineNumber - 1], leftLineNumber));
                    leftLineNumber++;
                    break;
                case DiffOperationKind.Insert:
                    addedBuffer.Add(new DiffLineEntry(rightLines[rightLineNumber - 1], rightLineNumber));
                    rightLineNumber++;
                    break;
            }
        }

        FlushChangedBlock();
        return rows.ToArray();
    }

    private static string BuildChangedChunkId(
        string repoRelativePath,
        int changedBlockIndex,
        int segmentIndex,
        GitDiffChunkBackgroundKind backgroundKind,
        IReadOnlyList<DirectDiffRow> segmentRows)
    {
        var firstRow = segmentRows[0];
        var leftStart = firstRow.LeftFileLineNumber ?? 0;
        var rightStart = firstRow.RightFileLineNumber ?? 0;
        var rowFingerprint = string.Join("|", segmentRows.Select(static row => row.RowId));
        return BuildStableId($"{repoRelativePath}:chunk:{changedBlockIndex}:{segmentIndex}:{backgroundKind}:{leftStart}:{rightStart}:{rowFingerprint}");
    }

    private static DiffOperation[] BuildLineOperations(IReadOnlyList<string> leftLines, IReadOnlyList<string> rightLines)
    {
        var leftCount = leftLines.Count;
        var rightCount = rightLines.Count;
        var max = leftCount + rightCount;
        var offset = max;
        var v = new int[(max * 2) + 3];
        Array.Fill(v, -1);
        v[offset + 1] = 0;
        var trace = new List<int[]>(max + 1);

        for (var d = 0; d <= max; d++)
        {
            var current = (int[])v.Clone();
            for (var k = -d; k <= d; k += 2)
            {
                var kIndex = offset + k;
                int x;
                if (k == -d || (k != d && current[kIndex - 1] < current[kIndex + 1]))
                {
                    x = current[kIndex + 1];
                }
                else
                {
                    x = current[kIndex - 1] + 1;
                }

                var y = x - k;
                while (x < leftCount &&
                       y < rightCount &&
                       string.Equals(leftLines[x], rightLines[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                current[kIndex] = x;
                if (x >= leftCount && y >= rightCount)
                {
                    trace.Add(current);
                    return BacktrackLineOperations(trace, leftCount, rightCount, offset);
                }
            }

            trace.Add(current);
            v = current;
        }

        throw new InvalidOperationException("Failed to compute direct line diff.");
    }

    private static DiffOperation[] BacktrackLineOperations(
        IReadOnlyList<int[]> trace,
        int leftCount,
        int rightCount,
        int offset)
    {
        var operations = new List<DiffOperation>(leftCount + rightCount);
        var x = leftCount;
        var y = rightCount;

        for (var d = trace.Count - 1; d > 0; d--)
        {
            var previous = trace[d - 1];
            var k = x - y;
            var previousK = k == -d || (k != d && previous[offset + k - 1] < previous[offset + k + 1])
                ? k + 1
                : k - 1;
            var previousX = previous[offset + previousK];
            var previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                operations.Add(new DiffOperation(DiffOperationKind.Equal));
                x--;
                y--;
            }

            if (x == previousX)
            {
                operations.Add(new DiffOperation(DiffOperationKind.Insert));
                y--;
            }
            else
            {
                operations.Add(new DiffOperation(DiffOperationKind.Delete));
                x--;
            }
        }

        while (x > 0 && y > 0)
        {
            operations.Add(new DiffOperation(DiffOperationKind.Equal));
            x--;
            y--;
        }

        while (x > 0)
        {
            operations.Add(new DiffOperation(DiffOperationKind.Delete));
            x--;
        }

        while (y > 0)
        {
            operations.Add(new DiffOperation(DiffOperationKind.Insert));
            y--;
        }

        operations.Reverse();
        return operations.ToArray();
    }

    private static (IReadOnlyList<GitInlineHighlightSpan> Left, IReadOnlyList<GitInlineHighlightSpan> Right) BuildInlineHighlights(string leftText, string rightText)
    {
        var leftTokens = Tokenize(leftText);
        var rightTokens = Tokenize(rightText);
        var prefix = 0;
        while (prefix < leftTokens.Count && prefix < rightTokens.Count && leftTokens[prefix].Text == rightTokens[prefix].Text)
        {
            prefix++;
        }

        var leftSuffix = leftTokens.Count - 1;
        var rightSuffix = rightTokens.Count - 1;
        while (leftSuffix >= prefix && rightSuffix >= prefix && leftTokens[leftSuffix].Text == rightTokens[rightSuffix].Text)
        {
            leftSuffix--;
            rightSuffix--;
        }

        if (prefix > leftSuffix && prefix > rightSuffix)
        {
            return ([], []);
        }

        var leftSpan = BuildSpan(leftTokens, prefix, Math.Max(prefix, leftSuffix));
        var rightSpan = BuildSpan(rightTokens, prefix, Math.Max(prefix, rightSuffix));
        return (
            leftSpan.Length > 0
                ? [new GitInlineHighlightSpan { StartColumn = leftSpan.Start, Length = leftSpan.Length, HighlightKind = GitInlineHighlightKind.Modified }]
                : [],
            rightSpan.Length > 0
                ? [new GitInlineHighlightSpan { StartColumn = rightSpan.Start, Length = rightSpan.Length, HighlightKind = GitInlineHighlightKind.Modified }]
                : []);
    }

    private static (int Start, int Length) BuildSpan(IReadOnlyList<TokenSlice> tokens, int startIndex, int endIndex)
    {
        if (tokens.Count is 0 || startIndex >= tokens.Count || endIndex < startIndex)
        {
            return default;
        }

        var start = tokens[startIndex].Start;
        var end = tokens[endIndex].Start + tokens[endIndex].Length;
        return (start, Math.Max(0, end - start));
    }

    private static List<TokenSlice> Tokenize(string text)
    {
        var tokens = new List<TokenSlice>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var start = 0;
        while (start < text.Length)
        {
            var category = GetTokenCategory(text[start]);
            var end = start + 1;
            while (end < text.Length && GetTokenCategory(text[end]) == category)
            {
                end++;
            }

            tokens.Add(new TokenSlice(text[start..end], start, end - start));
            start = end;
        }

        return tokens;
    }

    private static int GetTokenCategory(char value)
    {
        if (char.IsLetterOrDigit(value) || value == '_')
        {
            return 0;
        }

        return char.IsWhiteSpace(value) ? 1 : 2;
    }

    private static HunkRange ParseHunkRange(string hunkHeader)
    {
        var match = HunkHeaderRegex.Match(hunkHeader);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Unrecognized diff hunk header '{hunkHeader}'.");
        }

        return new HunkRange(
            int.Parse(match.Groups["oldStart"].Value),
            match.Groups["oldCount"].Success ? int.Parse(match.Groups["oldCount"].Value) : 1,
            int.Parse(match.Groups["newStart"].Value),
            match.Groups["newCount"].Success ? int.Parse(match.Groups["newCount"].Value) : 1);
    }

    private static string BuildPatchText(IReadOnlyList<string> fileHeaderLines, string header, IReadOnlyList<string> bodyLines)
    {
        var builder = new StringBuilder();
        foreach (var fileHeaderLine in fileHeaderLines)
        {
            builder.AppendLine(fileHeaderLine);
        }

        builder.AppendLine(header);
        foreach (var bodyLine in bodyLines)
        {
            builder.AppendLine(bodyLine);
        }

        return builder.ToString();
    }

    private sealed record TokenSlice(string Text, int Start, int Length);

    private sealed record CandidateRow(GitDiffDisplayRow Row, int Score);

    private sealed class ChunkActionBuilder(string chunkId)
    {
        private readonly HashSet<string> _lineActionIdSet = new(StringComparer.Ordinal);
        private readonly List<string> _lineActionIds = [];

        public string ChunkId { get; } = chunkId;
        public string FirstCanonicalRowId { get; private set; } = string.Empty;
        public string LastCanonicalRowId { get; private set; } = string.Empty;
        public int FirstDisplayIndex { get; private set; } = int.MaxValue;
        public int LastDisplayIndex { get; private set; } = int.MinValue;
        public IReadOnlyList<string> LineActionIds => _lineActionIds;

        public void Add(GitDiffDisplayRow row, string lineActionId)
        {
            if (_lineActionIdSet.Add(lineActionId))
            {
                _lineActionIds.Add(lineActionId);
            }

            if (row.DisplayIndex < FirstDisplayIndex)
            {
                FirstDisplayIndex = row.DisplayIndex;
                FirstCanonicalRowId = row.RowId;
            }

            if (row.DisplayIndex > LastDisplayIndex)
            {
                LastDisplayIndex = row.DisplayIndex;
                LastCanonicalRowId = row.RowId;
            }
        }
    }

    private sealed record DiffOperation(DiffOperationKind Kind);

    private enum DiffOperationKind
    {
        Equal,
        Delete,
        Insert
    }

    private enum SnapshotSide
    {
        Head,
        Index,
        Worktree
    }

    internal enum ActionAnchorSide
    {
        Head,
        Worktree
    }

    private sealed class MergedAlignmentRowBuilder(int sortAnchor, int sortKind, int sortOrdinal)
    {
        private string? _headText;
        private string? _indexText;
        private string? _worktreeText;

        public int? HeadLineNumber { get; private set; }
        public int? IndexLineNumber { get; private set; }
        public int? WorktreeLineNumber { get; private set; }
        public int SortAnchor { get; } = sortAnchor;
        public int SortKind { get; } = sortKind;
        public int SortOrdinal { get; } = sortOrdinal;

        public void Apply(SnapshotAlignmentRow row)
        {
            HeadLineNumber ??= row.HeadLineNumber;
            IndexLineNumber ??= row.IndexLineNumber;
            WorktreeLineNumber ??= row.WorktreeLineNumber;
            _headText ??= row.HeadText;
            _indexText ??= row.IndexText;
            _worktreeText ??= row.WorktreeText;
        }

        public MergedAlignmentRow Build(int alignmentOrdinal)
        {
            var headEqualsIndex = SnapshotEquals(HeadLineNumber, _headText, IndexLineNumber, _indexText);
            var indexEqualsWorktree = SnapshotEquals(IndexLineNumber, _indexText, WorktreeLineNumber, _worktreeText);
            var stageState = !headEqualsIndex && indexEqualsWorktree
                ? GitDiffRowStageState.Staged
                : headEqualsIndex && !indexEqualsWorktree
                    ? GitDiffRowStageState.Unstaged
                    : !headEqualsIndex && !indexEqualsWorktree
                        ? GitDiffRowStageState.Mixed
                        : GitDiffRowStageState.None;

            return new MergedAlignmentRow(
                HeadLineNumber,
                IndexLineNumber,
                WorktreeLineNumber,
                _headText,
                _indexText,
                _worktreeText,
                stageState,
                CanonicalChunkKey: $"{SortAnchor}:{SortKind}",
                ChunkLocalOrdinal: 0,
                AlignmentOrdinal: alignmentOrdinal);
        }

        private static bool SnapshotEquals(int? leftLineNumber, string? leftText, int? rightLineNumber, string? rightText)
        {
            if (leftLineNumber.HasValue != rightLineNumber.HasValue)
            {
                return false;
            }

            return !leftLineNumber.HasValue || string.Equals(leftText, rightText, StringComparison.Ordinal);
        }
    }

    private sealed record DirectDiffRow(
        string RowId,
        int DisplayIndex,
        string ChunkId,
        GitDiffDisplayRowKind Kind,
        string LeftText,
        string RightText,
        int? LeftFileLineNumber,
        int? RightFileLineNumber,
        bool IsSyntheticLeft,
        bool IsSyntheticRight,
        bool IsEditableRight,
        GitDiffChunkBackgroundKind ChunkBackgroundKind,
        IReadOnlyList<GitInlineHighlightSpan> InlineHighlightsLeft,
        IReadOnlyList<GitInlineHighlightSpan> InlineHighlightsRight)
    {
        public static implicit operator GitDiffDisplayRow(DirectDiffRow row)
        {
            return new GitDiffDisplayRow
            {
                RowId = row.RowId,
                DisplayIndex = row.DisplayIndex,
                ChunkId = row.ChunkId,
                Kind = row.Kind,
                LeftText = row.LeftText,
                RightText = row.RightText,
                LeftFileLineNumber = row.LeftFileLineNumber,
                RightFileLineNumber = row.RightFileLineNumber,
                IsSyntheticLeft = row.IsSyntheticLeft,
                IsSyntheticRight = row.IsSyntheticRight,
                IsEditableRight = row.IsEditableRight,
                ChunkBackgroundKind = row.ChunkBackgroundKind,
                InlineHighlightsLeft = row.InlineHighlightsLeft,
                InlineHighlightsRight = row.InlineHighlightsRight
            };
        }
    }

    private sealed class SnapshotAlignmentRow
    {
        public int? HeadLineNumber { get; init; }
        public int? IndexLineNumber { get; init; }
        public int? WorktreeLineNumber { get; init; }
        public string? HeadText { get; init; }
        public string? IndexText { get; init; }
        public string? WorktreeText { get; init; }
        public required int AnchorBeforeIndexLine { get; init; }
        public required int InsertionOrdinalAtAnchor { get; init; }
    }

    private sealed record MergedAlignmentRow(
        int? HeadLineNumber,
        int? IndexLineNumber,
        int? WorktreeLineNumber,
        string? HeadText,
        string? IndexText,
        string? WorktreeText,
        GitDiffRowStageState StageState,
        string CanonicalChunkKey,
        int ChunkLocalOrdinal,
        int AlignmentOrdinal);

    public sealed record RowStateProjection(
        IReadOnlyDictionary<string, GitDiffRowState> RowStatesByRowId,
        IReadOnlyList<string> MismatchedRowIds);

    internal sealed record ParsedDiffFile(string RepoRelativePath, IReadOnlyList<string> FileHeaderLines, IReadOnlyList<ParsedHunk> Hunks);

    internal sealed record ParsedHunk(string Header, HunkRange Range, IReadOnlyList<string> BodyLines);

    internal readonly record struct HunkRange(int OldStart, int OldCount, int NewStart, int NewCount);

    internal sealed record DiffLineEntry(string Text, int LineNumber);

    internal sealed record AtomicDiffChange(
        string LineActionId,
        string Header,
        IReadOnlyList<string> BodyLines,
        int? OldLineNumber,
        int? NewLineNumber,
        string? RemovedText,
        string? AddedText);
}
