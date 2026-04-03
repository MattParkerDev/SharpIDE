using ObservableCollections;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public static class SharpIdeSolutionModelExtensions
{
	public static SharpIdeProjectModel? GetProjectForContainingFolderPath(this SharpIdeSolutionModel solution, SharpIdeFolder folder)
	{
		var sharpIdeProject = solution.AllProjects.SingleOrDefault(s => s.DirectoryPath == folder.Path);
		return sharpIdeProject;
	}

	public static void UpdateFromNewSolution(this SharpIdeSolutionModel sharpIdeSolutionModel, SharpIdeSolutionModel newSharpIdeSolutionModel)
	{
		// TODO: Need to insert new nodes in correct position
		DiffProjects(sharpIdeSolutionModel.Projects, newSharpIdeSolutionModel.Projects);
		DiffSlnFolders(sharpIdeSolutionModel.SlnFolders, newSharpIdeSolutionModel.SlnFolders);

		sharpIdeSolutionModel.AllProjects.Clear();
		foreach (var project in CollectAllProjects(sharpIdeSolutionModel))
			sharpIdeSolutionModel.AllProjects.Add(project);
	}

	private static void UpdateSlnFolderFromNew(this SharpIdeSolutionFolder existing, SharpIdeSolutionFolder updated)
	{
		DiffProjects(existing.Projects, updated.Projects);
		DiffSlnFolders(existing.Folders, updated.Folders);
		DiffSlnFiles(existing.Files, updated.Files);
	}

	private static void DiffProjects(ObservableList<SharpIdeProjectModel> existing, ObservableList<SharpIdeProjectModel> updated)
	{
		// Projects whose Folder null-state changed: remove old, re-add new
		var changedExistingProjects = existing
			.Join(updated, p => p.FilePath, np => np.FilePath, (p, np) => (Old: p, New: np))
			.Where(pair => (pair.Old.Folder is null) != (pair.New.Folder is null))
			.ToList();

		List<SharpIdeProjectModel> toRemove = [..existing.ExceptBy(updated.Select(np => np.FilePath), p => p.FilePath), ..changedExistingProjects.Select(pair => pair.Old)];
		List<SharpIdeProjectModel> toAdd = [..updated.ExceptBy(existing.Select(p => p.FilePath), np => np.FilePath), ..changedExistingProjects.Select(pair => pair.New)];
		foreach (var s in toRemove) existing.Remove(s);
		foreach (var s in toAdd) existing.Insert(GetInsertionPosition(existing, s), s);
	}

	private static void DiffSlnFiles(ObservableList<SharpIdeSolutionFile> existing, ObservableList<SharpIdeSolutionFile> updated)
	{
		// Solution files whose .File null-state changed: remove old, re-add new
		var changedExistingSlnFiles = existing
			.Join(updated, p => p.Path, np => np.Path, (p, np) => (Old: p, New: np))
			.Where(pair => (pair.Old.File is null) != (pair.New.File is null))
			.ToList();
		List<SharpIdeSolutionFile> toRemove = [..existing.ExceptBy(updated.Select(np => np.Path), p => p.Path), ..changedExistingSlnFiles.Select(pair => pair.Old)];
		List<SharpIdeSolutionFile> toAdd = [..updated.ExceptBy(existing.Select(p => p.Path), np => np.Path), ..changedExistingSlnFiles.Select(pair => pair.New)];
		foreach (var s in toRemove) existing.Remove(s);
		foreach (var s in toAdd) existing.Insert(GetInsertionPosition(existing, s), s);
	}

	private static void DiffSlnFolders(ObservableList<SharpIdeSolutionFolder> existing, ObservableList<SharpIdeSolutionFolder> updated)
	{
		foreach (var existingFolder in existing)
		{
			var matchingNew = updated.SingleOrDefault(nf => nf.VsPersistencePath == existingFolder.VsPersistencePath);
			if (matchingNew is not null) existingFolder.UpdateSlnFolderFromNew(matchingNew);
		}

		var toRemove = existing.ExceptBy(updated.Select(nf => nf.VsPersistencePath), f => f.VsPersistencePath).ToList();
		var toAdd = updated.ExceptBy(existing.Select(f => f.VsPersistencePath), nf => nf.VsPersistencePath).ToList();
		foreach (var s in toRemove) existing.Remove(s);
		foreach (var s in toAdd) existing.Insert(GetInsertionPosition(existing, s), s);
	}

	private static IEnumerable<SharpIdeProjectModel> CollectAllProjects(SharpIdeSolutionModel solution)
	{
		foreach (var project in solution.Projects)
			yield return project;
		foreach (var folder in solution.SlnFolders)
		foreach (var project in CollectAllProjectsFromFolder(folder))
			yield return project;
	}

	private static IEnumerable<SharpIdeProjectModel> CollectAllProjectsFromFolder(SharpIdeSolutionFolder folder)
	{
		foreach (var project in folder.Projects)
			yield return project;
		foreach (var subFolder in folder.Folders)
		foreach (var project in CollectAllProjectsFromFolder(subFolder))
			yield return project;
	}

	private static int GetInsertionPosition(ObservableList<SharpIdeSolutionFile> files, SharpIdeSolutionFile solutionFile)
	{
		var correctInsertionPosition = files.list.BinarySearch(solutionFile, SharpIdeSolutionFileComparer.Instance);
		return GetInsertionIndexOrThrow(correctInsertionPosition);
	}
	private static int GetInsertionPosition(ObservableList<SharpIdeSolutionFolder> slnFolders, SharpIdeSolutionFolder slnFolder)
	{
		var correctInsertionPosition = slnFolders.list.BinarySearch(slnFolder, SharpIdeSolutionFolderComparer.Instance);
		return GetInsertionIndexOrThrow(correctInsertionPosition);
	}
	private static int GetInsertionPosition(ObservableList<SharpIdeProjectModel> projects, SharpIdeProjectModel project)
	{
		var correctInsertionPosition = projects.list.BinarySearch(project, SharpIdeProjectComparer.Instance);
		return GetInsertionIndexOrThrow(correctInsertionPosition);
	}

	private static int GetInsertionIndexOrThrow(int correctInsertionPosition)
	{
		if (correctInsertionPosition < 0)
		{
			correctInsertionPosition = ~correctInsertionPosition;
		}
		else
		{
			throw new InvalidOperationException("File or folder already exists in the containing folder");
		}

		return correctInsertionPosition;
	}
}
