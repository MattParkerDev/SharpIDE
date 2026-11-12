using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Razor.Utilities;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using AsyncBatchingWorkQueue = Microsoft.CodeAnalysis.Threading.AsyncBatchingWorkQueue;

namespace SharpIDE.Application.Features.FileWatching;

/// All file changes should go via this service
public class FileChangedService
{
	private readonly RoslynAnalysis _roslynAnalysis;
	private readonly SharpIdeSolutionService _sharpIdeSolutionService;
	private readonly IdeOpenTabsFileManager _openTabsFileManager;
	private readonly AsyncBatchingWorkQueue _updateSolutionDiagnosticsQueue;
	private readonly Lock _batchLock = new();
	private int _batchDepth;
	private bool _workspaceAlteredDuringBatch;
	private readonly List<WorkspaceFileOperation> _pendingFileOperations = [];
	// Processes one batch at a time, in order, so Roslyn always ends up with the latest text of each open file
	private readonly Microsoft.CodeAnalysis.Threading.AsyncBatchingWorkQueue<(SharpIdeFile File, SourceText Text)> _openFileTextChangedQueue;

	public FileChangedService(RoslynAnalysis roslynAnalysis, IdeOpenTabsFileManager openTabsFileManager, SharpIdeSolutionService sharpIdeSolutionService)
	{
		_roslynAnalysis = roslynAnalysis;
		_openTabsFileManager = openTabsFileManager;
		_sharpIdeSolutionService = sharpIdeSolutionService;
		_updateSolutionDiagnosticsQueue = new AsyncBatchingWorkQueue(TimeSpan.FromMilliseconds(200), ProcessBatchAsync, IAsynchronousOperationListener.Instance, CancellationToken.None);
		_openFileTextChangedQueue = new(TimeSpan.Zero, ProcessOpenFileTextChangesAsync, IAsynchronousOperationListener.Instance, CancellationToken.None);
	}

	public SharpIdeSolutionModel SolutionModel { get; set; } = null!;

	// Moves and removals apply to any file - e.g. a txt AdditionalFile from the project's evaluation - and are a no-op if Roslyn doesn't have it
	public Task SharpIdeFileRenamed(SharpIdeFile file, string oldFilePath) => ApplyOrDeferFileOperation(new WorkspaceFileOperation.Move(file, oldFilePath)); // TODO: handle csproj renamed

	public Task SharpIdeFileMoved(SharpIdeFile file, string oldFilePath) => ApplyOrDeferFileOperation(new WorkspaceFileOperation.Move(file, oldFilePath)); // TODO: handle csproj moved

	public async Task SharpIdeFileAdded(SharpIdeFile file, string content)
	{
		if (file.IsRoslynWorkspaceFile)
		{
			await ApplyOrDeferFileOperation(new WorkspaceFileOperation.Add(file, content));
		}
		// TODO: handle csproj added
	}

	public async Task SharpIdeFileRemoved(SharpIdeFile file)
	{
		await file.FileDeleted.InvokeParallelAsync();
		await ApplyOrDeferFileOperation(new WorkspaceFileOperation.Remove(file.Path));
	}

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

	private Task HandleProjectSystemFileSaved(SharpIdeFile file) => file switch
	{
		{ IsSlnFile: true } => HandleSlnChanged(file),
		{ IsCsprojFile: true } => HandleCsprojChanged(file),
		_ => Task.CompletedTask
	};

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
		lock (_batchLock)
		{
			if (--_batchDepth > 0) return;
			operations = [.._pendingFileOperations];
			_pendingFileOperations.Clear();
			workspaceAltered = _workspaceAlteredDuringBatch;
			_workspaceAlteredDuringBatch = false;
		}
		if (operations.Count > 0 && await _roslynAnalysis.ApplyFileOperations(operations)) workspaceAltered = true;
		if (workspaceAltered) OnWorkspaceAltered();
	}

	private async ValueTask ProcessBatchAsync(CancellationToken cancellationToken)
	{
		await _roslynAnalysis.UpdateSolutionDiagnostics(cancellationToken);
	}

	private async Task HandleCsprojChanged(SharpIdeFile file)
	{
		var project = SolutionModel.AllProjects.SingleOrDefault(p => p.FilePath == file.Path);
		if (project is null) return;
		project.MsBuildEvaluationProjectTask = project.LoadOrReloadProjectInMsBuild();
		await project.MsBuildEvaluationProjectTask;
		await _roslynAnalysis.ReloadProject(project, CancellationToken.None);
		OnWorkspaceAltered();
	}

	private async Task HandleSlnChanged(SharpIdeFile file)
	{
		if (FilePathNormalizer.AreFilePathsEquivalent(file.Path, SolutionModel.FilePath) is false) return;
		await _sharpIdeSolutionService.ReloadSolution();
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
