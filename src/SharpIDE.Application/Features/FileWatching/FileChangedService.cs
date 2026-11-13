using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Razor.Utilities;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using AsyncBatchingWorkQueue = Microsoft.CodeAnalysis.Threading.AsyncBatchingWorkQueue;

namespace SharpIDE.Application.Features.FileWatching;

/// All file changes should go via this service
public class FileChangedService
{
	private readonly RoslynAnalysis _roslynAnalysis;
	private readonly SharpIdeSolutionService _sharpIdeSolutionService;
	private readonly ProjectEvaluationService _projectEvaluationService;
	private readonly IdeOpenTabsFileManager _openTabsFileManager;
	private readonly AsyncBatchingWorkQueue _updateSolutionDiagnosticsQueue;
	private readonly Lock _batchLock = new();
	private int _batchDepth;
	private bool _workspaceAlteredDuringBatch;
	private readonly List<WorkspaceFileOperation> _pendingFileOperations = [];
	private bool _pendingSolutionReload;
	private readonly HashSet<SharpIdeProjectModel> _pendingProjectReloads = [];
	private readonly HashSet<string> _pendingImportFileChanges = new(PathComparison.Comparer);
	// Processes one batch at a time, in order, so Roslyn always ends up with the latest text of each open file
	private readonly Microsoft.CodeAnalysis.Threading.AsyncBatchingWorkQueue<(SharpIdeFile File, SourceText Text)> _openFileTextChangedQueue;

	public FileChangedService(RoslynAnalysis roslynAnalysis, IdeOpenTabsFileManager openTabsFileManager, SharpIdeSolutionService sharpIdeSolutionService, ProjectEvaluationService projectEvaluationService)
	{
		_projectEvaluationService = projectEvaluationService;
		_roslynAnalysis = roslynAnalysis;
		_openTabsFileManager = openTabsFileManager;
		_sharpIdeSolutionService = sharpIdeSolutionService;
		_updateSolutionDiagnosticsQueue = new AsyncBatchingWorkQueue(TimeSpan.FromMilliseconds(200), ProcessBatchAsync, IAsynchronousOperationListener.Instance, CancellationToken.None);
		_openFileTextChangedQueue = new(TimeSpan.Zero, ProcessOpenFileTextChangesAsync, IAsynchronousOperationListener.Instance, CancellationToken.None);
		_projectEvaluationService.ProjectsReevaluatedForImportChanges.Subscribe(OnProjectsReevaluatedForImportChanges);
	}

	/// Imported files (Directory.Build.props/targets, Directory.Packages.props, obj/*.nuget.g.props etc) changed, and the projects importing them were re-evaluated -
	/// their Roslyn projects (references, compiler options, analyzers, documents) come from a design-time build, so reload them too.
	/// Except projects whose last design-time build started after the files changed, which already saw them - e.g. the restore a (re)load runs before its design-time build writes obj/*.nuget.g.props.
	private async Task OnProjectsReevaluatedForImportChanges(IReadOnlyCollection<string> changedFilePaths, IReadOnlyList<SharpIdeProjectModel> projects)
	{
		// A deleted file has no write time - it changed just now
		var changedAtUtc = changedFilePaths.Max(path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow);
		var projectsToReload = projects.Where(project => _roslynAnalysis.IsProjectDesignTimeBuiltBefore(project, changedAtUtc)).ToList();
		if (projectsToReload.Count is 0) return;
		await _roslynAnalysis.ReloadProjects(projectsToReload, CancellationToken.None);
		// Reloaded projects read their documents from disk - put back the editors' text of open files, which may be unsaved
		await _roslynAnalysis.UpdateDocuments(_openTabsFileManager.GetOpenFileTexts());
		OnWorkspaceAltered();
	}

	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	// Moves and removals apply to any file - e.g. a txt AdditionalFile from the project's evaluation - and are a no-op if Roslyn doesn't have it
	public Task SharpIdeFileRenamed(SharpIdeFile file, string oldFilePath) => SharpIdeFileMoved(file, oldFilePath);

	public async Task SharpIdeFileMoved(SharpIdeFile file, string oldFilePath)
	{
		await ApplyOrDeferFileOperation(new WorkspaceFileOperation.Move(file, oldFilePath));
		// A project the solution references has moved away from (or onto) its path - IDE moves update the solution file to follow it first
		if (IsSolutionProjectPath(oldFilePath) || IsSolutionProjectPath(file.Path)) await ApplyOrDeferProjectSystemChange(reloadSolution: true);
		await ApplyOrDeferImportFileChanges(oldFilePath, file.Path);
	}

	public async Task SharpIdeFileAdded(SharpIdeFile file, string content)
	{
		if (file.IsRoslynWorkspaceFile)
		{
			await ApplyOrDeferFileOperation(new WorkspaceFileOperation.Add(file, content));
		}
		// e.g. a project the solution references, which was missing, has been checked out
		if (IsSolutionProjectPath(file.Path)) await ApplyOrDeferProjectSystemChange(reloadSolution: true);
		await ApplyOrDeferImportFileChanges(file.Path); // e.g. a new Directory.Build.props
	}

	public async Task SharpIdeFileRemoved(SharpIdeFile file)
	{
		await file.FileDeleted.InvokeParallelAsync();
		await ApplyOrDeferFileOperation(new WorkspaceFileOperation.Remove(file.Path));
		if (IsSolutionProjectPath(file.Path)) await ApplyOrDeferProjectSystemChange(reloadSolution: true); // Now shown as missing
		await ApplyOrDeferImportFileChanges(file.Path);
	}

	/// A csproj the solution references. Projects that aren't in the solution aren't loaded, so adding or removing one doesn't matter (as in VS).
	private bool IsSolutionProjectPath(string filePath) =>
		SolutionModel is not null &&
		filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
		SolutionModel.AllProjects.Any(p => FilePathNormalizer.AreFilePathsEquivalent(p.FilePath, filePath));

	public async Task AnalyzerDllFilesChanged(ImmutableArray<string> changedDllPaths)
	{
		var success = await _roslynAnalysis.ReloadProjectsWithAnyOfAnalyzerFileReferences(changedDllPaths);
		if (success is false) return;
		OnWorkspaceAltered();
	}

	/// Called on the UI thread by an open file's editor, whenever its text changes - including edits it was asked to make (completion, code actions, reloads from disk).
	/// The editor owns an open file's text; this records it, and forwards it to Roslyn in order, off the UI thread.
	/// newText should descend from the previous text via WithChanges, so Roslyn can reparse incrementally.
	public void OpenFileTextChanged(SharpIdeFile file, SourceText newText, bool isDirty)
	{
		if (_openTabsFileManager.TryUpdateFileTextInMemory(file, newText) is false) return;
		file.IsDirty.Value = isDirty;
		if (file.IsCsprojFile) return; // The project is reloaded from disk when the csproj is saved
		_openFileTextChangedQueue.AddWork((file, newText));
	}

	private async ValueTask ProcessOpenFileTextChangesAsync(ImmutableSegmentedList<(SharpIdeFile File, SourceText Text)> changes, CancellationToken cancellationToken)
	{
		// Only the latest text of each file matters - Roslyn can still reparse incrementally across several WithChanges steps
		var latestTextPerFile = new Dictionary<SharpIdeFile, SourceText>();
		foreach (var (file, text) in changes) latestTextPerFile[file] = text;

		var updatedAnything = await _roslynAnalysis.UpdateDocuments(latestTextPerFile.Select(s => (s.Key, s.Value)).ToList());
		if (updatedAnything) OnWorkspaceAltered();
	}

	/// Saves an open file, from its editor's text. Roslyn already has that text, so only the project system may need to react.
	public async Task SaveOpenFile(SharpIdeFile file)
	{
		await _openTabsFileManager.SaveFileAsync(file);
		await HandleProjectSystemFileSaved(file);
	}

	/// Files changed on disk, outside of the IDE (batched by the caller). Disk always wins.
	/// Closed files reach Roslyn in a single workspace change.
	public async Task ExternalFilesChanged(IReadOnlyList<(SharpIdeFile File, string NewContents)> changedFiles)
	{
		var byIsOpen = changedFiles.ToLookup(s => _openTabsFileManager.IsOpen(s.File));

		// The editor owns an open file's text, so the reload goes through it, and reaches Roslyn like any other edit
		await Task.WhenAll(byIsOpen[true].Select(s => s.File.EditRequested.InvokeParallelAsync(SharpIdeFileEditRequest.Reload(s.NewContents))));

		// AdditionalFiles such as txt files may have changed, so we need to attempt to update the workspace regardless of extension
		var closedFiles = byIsOpen[false]
			.Where(s => s.File.IsCsprojFile is false)
			.Select(s => (s.File, SourceText.From(s.NewContents, Encoding.UTF8)))
			.ToList();
		if (await _roslynAnalysis.UpdateDocuments(closedFiles)) OnWorkspaceAltered();

		foreach (var (file, _) in changedFiles)
		{
			await HandleProjectSystemFileSaved(file);
		}
	}

	/// Applies edits computed by Roslyn, e.g. by a code action or rename. Open files are edited via their editor (and left unsaved), closed files are written to disk.
	/// Closed files reach Roslyn in a single workspace change.
	public async Task ApplyTextEdits(IReadOnlyList<SharpIdeFileTextEdit> edits)
	{
		var byIsOpen = edits.ToLookup(s => _openTabsFileManager.IsOpen(s.File));

		await Task.WhenAll(byIsOpen[true].Select(s => s.File.EditRequested.InvokeParallelAsync(SharpIdeFileEditRequest.ApplyChanges(s.OldText, s.Changes))));

		var closedFiles = byIsOpen[false]
			.Select(s => (s.File, Text: s.OldText.WithChanges(s.Changes)))
			.ToList();
		await Task.WhenAll(closedFiles.Select(s => _openTabsFileManager.WriteClosedFileAsync(s.File, s.Text)));
		if (await _roslynAnalysis.UpdateDocuments(closedFiles)) OnWorkspaceAltered();
	}

	private Task HandleProjectSystemFileSaved(SharpIdeFile file)
	{
		if (SolutionModel is null) return Task.CompletedTask;
		if (file.IsSlnFile && FilePathNormalizer.AreFilePathsEquivalent(file.Path, SolutionModel.FilePath)) return ApplyOrDeferProjectSystemChange(reloadSolution: true);
		if (file.IsCsprojFile && SolutionModel.AllProjects.FirstOrDefault(p => FilePathNormalizer.AreFilePathsEquivalent(p.FilePath, file.Path)) is { } project)
		{
			return ApplyOrDeferProjectSystemChange(reloadSolution: false, project);
		}
		return ApplyOrDeferImportFileChanges(file.Path);
	}

	/// Directory.Build.props/targets, Directory.Packages.props etc - the projects that import them (or may now) are re-evaluated. Ignores other files.
	private async Task ApplyOrDeferImportFileChanges(params string[] filePaths)
	{
		List<string> importFilePaths = [];
		foreach (var filePath in filePaths)
		{
			if (ProjectEvaluationService.IsMsBuildImportFile(filePath)) importFilePaths.Add(filePath);
		}
		if (importFilePaths.Count is 0 || SolutionModel is null) return;
		lock (_batchLock)
		{
			if (_batchDepth > 0)
			{
				_pendingImportFileChanges.UnionWith(importFilePaths);
				return;
			}
		}
		await _projectEvaluationService.ReloadForChangedFilesAsync(importFilePaths);
	}

	/// Applied now, or once at the end of the batch - so e.g. a git checkout changing the sln and several csproj files reloads once
	private async Task ApplyOrDeferProjectSystemChange(bool reloadSolution, SharpIdeProjectModel? projectToReload = null)
	{
		lock (_batchLock)
		{
			if (_batchDepth > 0)
			{
				_pendingSolutionReload |= reloadSolution;
				if (projectToReload is not null) _pendingProjectReloads.Add(projectToReload);
				return;
			}
		}
		await ApplyProjectSystemChanges(reloadSolution, projectToReload is null ? [] : [projectToReload]);
	}

	private async Task ApplyProjectSystemChanges(bool reloadSolution, IReadOnlyCollection<SharpIdeProjectModel> projectsToReload)
	{
		if (reloadSolution is false && projectsToReload.Count is 0) return;
		if (reloadSolution)
		{
			// Re-reads the solution file (adding, removing and re-resolving projects), and reloads every project in Roslyn
			await _sharpIdeSolutionService.ReloadSolution();
			// Projects kept by the reload keep their MSBuild evaluation, so re-evaluate any whose csproj changed too
			var keptProjects = projectsToReload.Where(SolutionModel.AllProjects.Contains).ToList();
			await _projectEvaluationService.EvaluateProjectsAsync(keptProjects);
		}
		else
		{
			await _projectEvaluationService.EvaluateProjectsAsync(projectsToReload);
			await _roslynAnalysis.ReloadProjects(projectsToReload, CancellationToken.None);
		}
		// Reloaded projects read their documents from disk - put back the editors' text of open files, which may be unsaved
		await _roslynAnalysis.UpdateDocuments(_openTabsFileManager.GetOpenFileTexts());
		OnWorkspaceAltered();
	}

	private void OnWorkspaceAltered()
	{
		lock (_batchLock)
		{
			if (_batchDepth > 0)
			{
				_workspaceAlteredDuringBatch = true;
				return;
			}
		}
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
	}

	/// Files added, removed and moved during the batch reach the workspace in one change, and SolutionAltered is raised once -
	/// so e.g. a branch switch, or deleting a folder, doesn't update the workspace and re-analyse per file. Batches may nest.
	public FileChangeBatch BeginBatch()
	{
		lock (_batchLock) _batchDepth++;
		return new FileChangeBatch(this);
	}

	internal Task ApplyPendingFileOperationsForBatch() => ApplyPendingFileOperations();

	internal async Task EndBatchAsync()
	{
		List<WorkspaceFileOperation> operations;
		bool workspaceAltered;
		bool reloadSolution;
		List<SharpIdeProjectModel> projectsToReload;
		List<string> importFileChanges;
		lock (_batchLock)
		{
			if (--_batchDepth > 0) return;
			operations = [.._pendingFileOperations];
			_pendingFileOperations.Clear();
			workspaceAltered = _workspaceAlteredDuringBatch;
			_workspaceAlteredDuringBatch = false;
			reloadSolution = _pendingSolutionReload;
			_pendingSolutionReload = false;
			projectsToReload = [.._pendingProjectReloads];
			_pendingProjectReloads.Clear();
			importFileChanges = [.._pendingImportFileChanges];
			_pendingImportFileChanges.Clear();
		}
		if (operations.Count > 0 && await _roslynAnalysis.ApplyFileOperations(operations)) workspaceAltered = true;
		// After the file operations, so a project reload sees the files' current paths
		await ApplyProjectSystemChanges(reloadSolution, projectsToReload);
		// After project file reloads, so their evaluations aren't redone
		if (importFileChanges.Count > 0) await _projectEvaluationService.ReloadForChangedFilesAsync(importFileChanges);
		if (workspaceAltered) OnWorkspaceAltered();
	}

	private async ValueTask ProcessBatchAsync(CancellationToken cancellationToken)
	{
		await _roslynAnalysis.UpdateSolutionDiagnostics(cancellationToken);
	}

	/// Applied now, or with the rest of the batch if one is in progress
	private async Task ApplyOrDeferFileOperation(WorkspaceFileOperation operation)
	{
		lock (_batchLock)
		{
			if (_batchDepth > 0)
			{
				_pendingFileOperations.Add(operation);
				return;
			}
		}
		if (await _roslynAnalysis.ApplyFileOperations([operation])) OnWorkspaceAltered();
	}

	private async Task ApplyPendingFileOperations()
	{
		List<WorkspaceFileOperation> operations;
		lock (_batchLock)
		{
			operations = [.._pendingFileOperations];
			_pendingFileOperations.Clear();
		}
		if (operations.Count > 0 && await _roslynAnalysis.ApplyFileOperations(operations)) OnWorkspaceAltered();
	}
}

/// While in progress, files added, removed and moved are applied to the workspace together, and SolutionAltered is raised once, when it ends.
public sealed class FileChangeBatch : IAsyncDisposable
{
	private readonly FileChangedService _service;
	private int _disposed;

	internal FileChangeBatch(FileChangedService service) => _service = service;

	/// Applies the files added, removed and moved so far, in one workspace change - e.g. before updating the text of files that were just moved
	public Task ApplyPendingFileOperationsAsync() => _service.ApplyPendingFileOperationsForBatch();

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) is 0) await _service.EndBatchAsync();
	}
}

public static class NullOperationListenerExtensions
{
	private static readonly IAsynchronousOperationListener _nullOperationListener = new AsynchronousOperationListenerProvider.NullOperationListener();
	extension(IAsynchronousOperationListener nullOperationListener)
	{
		public static IAsynchronousOperationListener Instance => _nullOperationListener;
	}
}
