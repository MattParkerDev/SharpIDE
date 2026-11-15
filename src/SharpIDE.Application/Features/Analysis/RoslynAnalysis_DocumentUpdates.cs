using System.Collections.Immutable;
using System.Text;
using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
	/// Returns true if the document was found and its text changed, otherwise false.
	/// For incremental parsing, newText should descend (via WithChanges) from the text currently in the workspace.
	public Task<bool> UpdateDocument(SharpIdeFile fileModel, SourceText newText) => UpdateDocuments([(fileModel, newText)]);

	/// Updates several files' text in a single workspace change, so features never see a mix of old and new files.
	/// Returns true if any document was found and its text changed.
	public Task<bool> UpdateDocuments(IReadOnlyCollection<(SharpIdeFile File, SourceText Text)> files) => UpdateDocuments(files.Select(s => (s.File.Path, s.Text)).ToList());

	/// By path - for files not in the IDE's file tree, e.g. an .editorconfig above the solution folder
	public async Task<bool> UpdateDocuments(IReadOnlyCollection<(string FilePath, SourceText Text)> files)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(UpdateDocuments)}");
		await _solutionLoadedTcs.Task;

		var currentSolution = _workspace!.CurrentSolution;
		var changedFiles = new List<(string FilePath, SourceText Text)>(files.Count);
		DocumentId? firstChangedDocumentId = null;
		foreach (var (filePath, newText) in files)
		{
			var documentIds = currentSolution.GetDocumentIdsWithFilePath(filePath);
			if (documentIds.IsEmpty) continue;
			// Skip no-ops, e.g. an editor opening a file whose text the workspace already has. Usually cheap - differing lengths return immediately.
			if (currentSolution.GetTextDocument(documentIds[0]) is { } existingDocument &&
				existingDocument.TryGetText(out var existingText) &&
				(ReferenceEquals(existingText, newText) || existingText.ContentEquals(newText)))
			{
				continue;
			}
			changedFiles.Add((filePath, newText));
			firstChangedDocumentId ??= documentIds[0];
		}
		if (changedFiles.Count is 0) return false;

		// Atomic: the transformation is re-run against the latest solution if another change lands first, so concurrent updates can't be lost.
		// Linked documents (the same file in several projects, e.g. one per TFM) all get the new text.
		var changeKind = changedFiles.Count is 1 ? WorkspaceChangeKind.DocumentChanged : WorkspaceChangeKind.SolutionChanged;
		return _workspace.SetCurrentSolution(oldSolution =>
		{
			var newSolution = oldSolution;
			foreach (var (filePath, newText) in changedFiles)
			{
				foreach (var documentId in oldSolution.GetDocumentIdsWithFilePath(filePath))
				{
					if (newSolution.ContainsDocument(documentId)) newSolution = newSolution.WithDocumentText(documentId, newText, PreservationMode.PreserveIdentity);
					else if (newSolution.ContainsAdditionalDocument(documentId)) newSolution = newSolution.WithAdditionalDocumentText(documentId, newText, PreservationMode.PreserveIdentity);
					else if (newSolution.ContainsAnalyzerConfigDocument(documentId)) newSolution = newSolution.WithAnalyzerConfigDocumentText(documentId, newText, PreservationMode.PreserveIdentity);
				}
			}
			return newSolution;
		}, changeKind, documentId: changedFiles.Count is 1 ? firstChangedDocumentId : null);
	}

	/// Applies file additions, removals and moves in a single atomic workspace change, in order.
	/// Returns true if the workspace changed.
	public async Task<bool> ApplyFileOperations(IReadOnlyList<WorkspaceFileOperation> operations)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(ApplyFileOperations)}");
		await _solutionLoadedTcs.Task;
		if (operations.Count is 0) return false;

		// Everything needing the project model, async work or new ids is resolved first - the transformation may be re-run, so must be pure
		var currentSolution = _workspace!.CurrentSolution;
		var steps = new List<Func<Solution, Solution>>(operations.Count);
		DocumentId? singleDocumentId = null;
		foreach (var operation in operations)
		{
			switch (operation)
			{
				case WorkspaceFileOperation.Add { File.IsEditorConfigFile: true } add:
					{
						// Applies to every project at or below its folder - as MSBuild's EditorConfigFiles, from the project's folder up
						var directory = Path.GetDirectoryName(add.File.Path)!;
						var documentIds = currentSolution.Projects
							.Where(p => p.FilePath is not null && PathComparison.IsSameOrUnder(Path.GetDirectoryName(p.FilePath)!, directory))
							.Select(p => DocumentId.CreateNewId(p.Id))
							.ToList();
						if (documentIds.Count is 0) break;
						singleDocumentId = documentIds[0];
						var text = SourceText.From(add.Content, Encoding.UTF8);
						var (name, path) = (add.File.Name.Value, add.File.Path);
						steps.Add(solution => AddEditorConfigToSolution(solution, documentIds, name, path, text));
						break;
					}
				case WorkspaceFileOperation.Add add:
					{
						if (GetProjectIdForNewFile(add.File) is not { } projectId) break;
						var documentId = DocumentId.CreateNewId(projectId);
						singleDocumentId = documentId;
						var text = SourceText.From(add.Content, Encoding.UTF8);
						var (name, path, isCsharp) = (add.File.Name.Value, add.File.Path, add.File.IsCsharpFile);
						steps.Add(solution => AddFileToSolution(solution, documentId, name, path, text, isCsharp));
						break;
					}
				case WorkspaceFileOperation.Remove remove:
					{
						singleDocumentId = currentSolution.GetDocumentIdsWithFilePath(remove.FilePath).FirstOrDefault();
						var path = remove.FilePath;
						steps.Add(solution => RemoveFileFromSolution(solution, path));
						break;
					}
				case WorkspaceFileOperation.Move move:
					{
						var existingIds = currentSolution.GetDocumentIdsWithFilePath(move.OldFilePath);
						singleDocumentId = existingIds.FirstOrDefault();
						// Other files (e.g. a txt AdditionalFile, or .editorconfig) came from the project's evaluation - keep them in their project
						var targetProjectId = move.File.IsRoslynWorkspaceFile ? GetProjectIdForNewFile(move.File) : existingIds.FirstOrDefault()?.ProjectId;
						// Only needed if it can't stay the same document - e.g. moved to another project, or it's an additional document
						SourceText? text = null;
						if (existingIds.IsEmpty || existingIds.Any(id => id.ProjectId != targetProjectId || currentSolution.ContainsDocument(id) is false))
						{
							text = existingIds.IsEmpty is false && currentSolution.GetTextDocument(existingIds[0]) is { } existingDocument
								? await existingDocument.GetTextAsync()
								: File.Exists(move.File.Path) ? SourceText.From(await File.ReadAllTextAsync(move.File.Path), Encoding.UTF8) : null;
						}
						var newDocumentId = targetProjectId is null ? null : DocumentId.CreateNewId(targetProjectId);
						var (oldPath, name, path, isCsharp) = (move.OldFilePath, move.File.Name.Value, move.File.Path, move.File.IsCsharpFile);
						steps.Add(solution => MoveFileInSolution(solution, oldPath, name, path, isCsharp, targetProjectId, newDocumentId, text));
						break;
					}
			}
		}
		if (steps.Count is 0) return false;

		var changeKind = operations.Count is 1
			? operations[0] switch
			{
				WorkspaceFileOperation.Add => WorkspaceChangeKind.DocumentAdded,
				WorkspaceFileOperation.Remove => WorkspaceChangeKind.DocumentRemoved,
				_ => WorkspaceChangeKind.DocumentInfoChanged,
			}
			: WorkspaceChangeKind.SolutionChanged;
		return _workspace.SetCurrentSolution(
			oldSolution => steps.Aggregate(oldSolution, (solution, step) => step(solution)),
			changeKind,
			documentId: changeKind is WorkspaceChangeKind.SolutionChanged ? null : singleDocumentId);
	}

	private static Solution AddFileToSolution(Solution solution, DocumentId documentId, string name, string filePath, SourceText text, bool isCsharp, bool isAnalyzerConfig = false)
	{
		if (solution.ContainsProject(documentId.ProjectId) is false) return solution;
		if (solution.GetDocumentIdsWithFilePath(filePath).IsEmpty is false) return solution; // Already there, e.g. added by a project reload
		if (isCsharp) return solution.AddDocument(documentId, name, text, filePath: filePath);
		if (isAnalyzerConfig is false) return solution.AddAdditionalDocument(documentId, name, text, filePath: filePath);
		var loader = TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), filePath));
		return solution.AddAnalyzerConfigDocuments([DocumentInfo.Create(documentId, name, loader: loader, filePath: filePath)]);
	}

	private static Solution AddEditorConfigToSolution(Solution solution, IReadOnlyList<DocumentId> documentIds, string name, string filePath, SourceText text)
	{
		var loader = TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), filePath));
		var documentInfos = documentIds
			.Where(id => solution.GetProject(id.ProjectId) is { } project && project.AnalyzerConfigDocuments.Any(d => PathComparison.PathEquals(d.FilePath, filePath)) is false)
			.Select(id => DocumentInfo.Create(id, name, loader: loader, filePath: filePath))
			.ToImmutableArray();
		return documentInfos.IsEmpty ? solution : solution.AddAnalyzerConfigDocuments(documentInfos);
	}

	private static Solution RemoveFileFromSolution(Solution solution, string filePath)
	{
		var documentIds = solution.GetDocumentIdsWithFilePath(filePath);
		if (documentIds.IsEmpty) return solution;
		return solution
			.RemoveDocuments([.. documentIds.Where(solution.ContainsDocument)])
			.RemoveAdditionalDocuments([.. documentIds.Where(solution.ContainsAdditionalDocument)])
			.RemoveAnalyzerConfigDocuments([.. documentIds.Where(solution.ContainsAnalyzerConfigDocument)]);
	}

	private static Solution MoveFileInSolution(Solution solution, string oldFilePath, string name, string filePath, bool isCsharp, ProjectId? targetProjectId, DocumentId? newDocumentId, SourceText? text)
	{
		var documentIds = solution.GetDocumentIdsWithFilePath(oldFilePath);
		// The usual case: regular documents staying in their project keep their identity, so nothing is reparsed. Also any linked copies (e.g. one per target framework).
		if (documentIds.Any(id => id.ProjectId == targetProjectId) && documentIds.All(solution.ContainsDocument))
		{
			foreach (var documentId in documentIds)
			{
				solution = solution.WithDocumentFilePath(documentId, filePath).WithDocumentName(documentId, name);
			}
			return solution;
		}
		// Otherwise (moved to another project, or not a regular document - only those can change path in place) remove it, and add it where it belongs now
		var isAnalyzerConfig = documentIds.Any(solution.ContainsAnalyzerConfigDocument);
		solution = RemoveFileFromSolution(solution, oldFilePath);
		if (newDocumentId is null || text is null) return solution;
		return AddFileToSolution(solution, newDocumentId, name, filePath, text, isCsharp, isAnalyzerConfig);
	}

	/// The project a file at this path belongs to, by its containing project folder and that project's globs. Null if none.
	private ProjectId? GetProjectIdForNewFile(SharpIdeFile fileModel)
	{
		var sharpIdeProject = _sharpIdeSolutionModel!.GetProjectByContainingFolder(fileModel);
		if (sharpIdeProject is null) return null;
		var probableProject = GetProjectForSharpIdeProjectModel(sharpIdeProject);
		// This file probably belongs to this project, but we need to check its path against the globs for the project to make sure
		var projectFileInfo = _projectFileInfoMap.GetValueOrDefault(probableProject.Id);
		Guard.Against.Null(projectFileInfo);

		var generatedFilesOutputDirectory = projectFileInfo.GeneratedFilesOutputDirectory;
		if (generatedFilesOutputDirectory is not null && fileModel.Path.StartsWith(generatedFilesOutputDirectory, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		// This may not be perfect, as None Include="" seems to be returned here as one of the globs as Include, with no distinction of Compile vs None etc
		// TODO: Investigate getting the glob type (Compile, None, etc)
		var matchers = projectFileInfo.FileGlobs.Select(glob =>
		{
			var matcher = new Matcher();
			matcher.AddIncludePatterns(glob.Includes);
			matcher.AddExcludePatterns(glob.Excludes);
			matcher.AddExcludePatterns(glob.Removes);
			return matcher;
		});

		var belongsToProject = false;
		// Check if the file path matches any of the globs in the project file.
		foreach (var matcher in matchers)
		{
			// CPS re-creates the msbuild globs from the includes/excludes/removes and the project XML directory and
			// ignores the MSBuildGlob.FixedDirectoryPart.  We'll do the same here and match using the project directory as the relative path.
			// See https://devdiv.visualstudio.com/DevDiv/_git/CPS?path=/src/Microsoft.VisualStudio.ProjectSystem/Build/MsBuildGlobFactory.cs
			var relativeDirectory = sharpIdeProject.DirectoryPath;

			var matches = matcher.Match(relativeDirectory, fileModel.Path);
			if (matches.HasMatches)
			{
				belongsToProject = true;
				break;
			}
		}

		if (belongsToProject is false)
		{
			return null;
		}
		return probableProject.Id;
	}
}
