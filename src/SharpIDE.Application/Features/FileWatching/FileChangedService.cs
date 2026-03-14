using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.FileWatching;

public enum FileChangeType
{
	IdeSaveToDisk, // Apply to disk
	IdeUnsavedChange, // Apply only in memory
	ExternalChange, // Apply to disk, as well as in memory
	CodeActionChange, // Apply to disk, as well as in memory
	CompletionChange // Apply only in memory, as well as notify tabs of new content
}

public class FileChangedService
{
	private readonly RoslynAnalysis _roslynAnalysis;
	private readonly IdeOpenTabsFileManager _openTabsFileManager;
	private readonly AsyncBatchingWorkQueue _updateSolutionDiagnosticsQueue;

	public FileChangedService(RoslynAnalysis roslynAnalysis, IdeOpenTabsFileManager openTabsFileManager)
	{
		_roslynAnalysis = roslynAnalysis;
		_openTabsFileManager = openTabsFileManager;
		_updateSolutionDiagnosticsQueue = new AsyncBatchingWorkQueue(TimeSpan.FromMilliseconds(200), ProcessBatchAsync, IAsynchronousOperationListener.Instance, CancellationToken.None);
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

	// All file changes should go via this service
	public async Task SharpIdeFileChanged(SharpIdeFile file, string newContents, FileChangeType changeType, SharpIdeFileLinePosition? linePosition = null)
	{
		if (changeType is FileChangeType.ExternalChange)
		{
			// Disk is already up to date
			// Update any open tabs
			// update in memory
			await _openTabsFileManager.UpdateFileTextInMemoryIfOpen(file, newContents);
			file.FileContentsChangedExternally.InvokeParallelFireAndForget(linePosition);
		}
		else if (changeType is FileChangeType.CodeActionChange)
		{
			// update in memory, tabs and save to disk
			await _openTabsFileManager.UpdateInMemoryIfOpenAndSaveAsync(file, newContents);
			file.FileContentsChangedExternally.InvokeParallelFireAndForget(linePosition);
		}
		else if (changeType is FileChangeType.CompletionChange)
		{
			// update in memory, tabs
			await _openTabsFileManager.UpdateFileTextInMemory(file, newContents);
			file.FileContentsChangedExternally.InvokeParallelFireAndForget(linePosition);
		}
		else if (changeType is FileChangeType.IdeSaveToDisk)
		{
			// save to disk
			// We technically don't need to update in memory here. TODO review
			await _openTabsFileManager.UpdateInMemoryIfOpenAndSaveAsync(file, newContents);
		}
		else if (changeType is FileChangeType.IdeUnsavedChange)
		{
			// update in memory only
			await _openTabsFileManager.UpdateFileTextInMemory(file, newContents);
		}
		var afterSaveTask = (file, changeType) switch
		{
			({ IsCsprojFile: true }, FileChangeType.IdeSaveToDisk or FileChangeType.ExternalChange) => HandleCsprojChanged(file),
			({ IsCsprojFile: true }, _) => Task.CompletedTask,
			(_, _) => HandlePotentialWorkspaceFile_Changed(file, newContents)
		};
		await afterSaveTask;
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

	/// AdditionalFiles such as txt files may have changed, so we need to attempt to update the workspace regardless of extension
	private async Task HandlePotentialWorkspaceFile_Changed(SharpIdeFile file, string newContents)
	{
		var fileUpdatedInWorkspace = await _roslynAnalysis.UpdateDocument(file, newContents);
		if (fileUpdatedInWorkspace is false) return;
		GlobalEvents.Instance.SolutionAltered.InvokeParallelFireAndForget();
		_updateSolutionDiagnosticsQueue.AddWork();
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
