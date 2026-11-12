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

	public async Task SharpIdeFileRenamed(SharpIdeFile file, string oldFilePath)
	{
		if (file.IsRoslynWorkspaceFile)
		{
			await HandleWorkspaceFileRenamed(file, oldFilePath);
		}
		// TODO: handle csproj moved
	}

	public async Task SharpIdeFileMoved(SharpIdeFile file, string oldFilePath)
	{
		if (file.IsRoslynWorkspaceFile)
		{
			await HandleWorkspaceFileMoved(file, oldFilePath);
		}
		// TODO: handle csproj moved
	}

	public async Task SharpIdeFileAdded(SharpIdeFile file, string content)
	{
		if (file.IsRoslynWorkspaceFile)
		{
			await HandleWorkspaceFileAdded(file, content);
		}
		// TODO: handle csproj added
	}

	public async Task SharpIdeFileRemoved(SharpIdeFile file)
	{
		await file.FileDeleted.InvokeParallelAsync();
		if (file.IsRoslynWorkspaceFile)
		{
			await HandleWorkspaceFileRemoved(file);
		}
	}

	public async Task AnalyzerDllFilesChanged(ImmutableArray<string> changedDllPaths)
	{
		var success = await _roslynAnalysis.ReloadProjectsWithAnyOfAnalyzerFileReferences(changedDllPaths);
		if (success is false) return;
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
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

		var updatedAnything = false;
		foreach (var (file, text) in latestTextPerFile)
		{
			updatedAnything |= await _roslynAnalysis.UpdateDocument(file, text);
		}
		if (updatedAnything) OnWorkspaceAltered();
	}

	/// Saves an open file, from its editor's text. Roslyn already has that text, so only the project system may need to react.
	public async Task SaveOpenFile(SharpIdeFile file)
	{
		await _openTabsFileManager.SaveFileAsync(file);
		await HandleProjectSystemFileSaved(file);
	}

	/// The file changed on disk, outside of the IDE. Disk always wins.
	public async Task ExternalFileChanged(SharpIdeFile file, string newContents)
	{
		if (_openTabsFileManager.IsOpen(file))
		{
			// The editor owns an open file's text, so the reload goes through it, and reaches Roslyn like any other edit
			await file.EditRequested.InvokeParallelAsync(SharpIdeFileEditRequest.Reload(newContents));
		}
		else if (file.IsCsprojFile is false)
		{
			// AdditionalFiles such as txt files may have changed, so we need to attempt to update the workspace regardless of extension
			var updated = await _roslynAnalysis.UpdateDocument(file, SourceText.From(newContents, Encoding.UTF8));
			if (updated) OnWorkspaceAltered();
		}
		await HandleProjectSystemFileSaved(file);
	}

	/// Applies edits computed by Roslyn, e.g. by a code action or rename. Open files are edited via their editor (and left unsaved), closed files are written to disk.
	public async Task ApplyTextEdits(IReadOnlyList<SharpIdeFileTextEdit> edits)
	{
		var updatedAnything = false;
		foreach (var edit in edits)
		{
			if (_openTabsFileManager.IsOpen(edit.File))
			{
				await edit.File.EditRequested.InvokeParallelAsync(SharpIdeFileEditRequest.ApplyChanges(edit.OldText, edit.Changes));
				continue;
			}
			var newText = edit.OldText.WithChanges(edit.Changes);
			await _openTabsFileManager.WriteClosedFileAsync(edit.File, newText);
			updatedAnything |= await _roslynAnalysis.UpdateDocument(edit.File, newText);
		}
		if (updatedAnything) OnWorkspaceAltered();
	}

	private Task HandleProjectSystemFileSaved(SharpIdeFile file) => file switch
	{
		{ IsSlnFile: true } => HandleSlnChanged(file),
		{ IsCsprojFile: true } => HandleCsprojChanged(file),
		_ => Task.CompletedTask
	};

	private void OnWorkspaceAltered()
	{
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
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
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
	}

	private async Task HandleSlnChanged(SharpIdeFile file)
	{
		if (FilePathNormalizer.AreFilePathsEquivalent(file.Path, SolutionModel.FilePath) is false) return;
		await _sharpIdeSolutionService.ReloadSolution();
	}

	private async Task HandleWorkspaceFileAdded(SharpIdeFile file, string contents)
	{
		var success = await _roslynAnalysis.AddDocument(file, contents);
		if (success is false) return;
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
	}

	private async Task HandleWorkspaceFileRemoved(SharpIdeFile file)
	{
		var success = await _roslynAnalysis.RemoveDocument(file);
		if (success is false) return;
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
	}

	private async Task HandleWorkspaceFileMoved(SharpIdeFile file, string oldFilePath)
	{
		await _roslynAnalysis.MoveDocument(file, oldFilePath);
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
	}

	private async Task HandleWorkspaceFileRenamed(SharpIdeFile file, string oldFilePath)
	{
		await _roslynAnalysis.RenameDocument(file, oldFilePath);
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
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
