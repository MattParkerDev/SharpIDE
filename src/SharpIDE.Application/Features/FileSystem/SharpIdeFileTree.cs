using System.Collections.Concurrent;
using System.Collections.Immutable;
using ObservableCollections;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileSystem;

/// The files and folders on disk that make up the solution: one tree per root folder - the solution's folder, plus the folder of each project outside it
/// (e.g. src/App.sln referencing ../tests/App.Tests.csproj, or a project in another repository).
/// Separate roots rather than one common ancestor, so a project elsewhere doesn't mean enumerating and watching e.g. a whole Repos folder.
public sealed class SharpIdeFileTree
{
	private readonly Lock _rootsLock = new();

	public ImmutableArray<SharpIdeRootFolder> Roots { get; private set; } = [];
	public ConcurrentDictionary<string, SharpIdeFile> AllFiles { get; } = new(PathComparison.Comparer);
	/// Every folder below the roots - not the roots themselves
	public ObservableList<SharpIdeFolder> AllFolders { get; } = [];

	/// Raised after the solution is read, as its projects (and so the roots) may have changed - also if a root's path changed (e.g. a project folder was renamed)
	public event Action? RootsChanged;

	public bool IsRoot(SharpIdeFolder folder) => folder is SharpIdeRootFolder root && Roots.Contains(root);
	public SharpIdeRootFolder? FindRoot(string path) => Roots.FirstOrDefault(r => PathComparison.PathEquals(r.Path, path));

	/// Returns the SharpIdeFolder that directly contains the given .csproj file - possibly a root folder.
	public SharpIdeFolder? GetFolderForProject(string csprojFullPath)
	{
		var csprojFile = AllFiles.GetValueOrDefault(csprojFullPath);
		if (csprojFile is null) return null;
		return csprojFile.Parent as SharpIdeFolder ?? throw new InvalidOperationException($"Parent of '{csprojFullPath}' is not a SharpIdeFolder.");
	}

	/// Adds roots for folders not yet covered, and removes roots no longer needed. Existing roots are kept as they are, so open files keep their SharpIdeFiles.
	internal void UpdateRoots(string solutionFilePath, IEnumerable<string> projectFilePaths)
	{
		var rootPaths = GetRootPaths(Path.GetDirectoryName(Path.GetFullPath(solutionFilePath))!, projectFilePaths);
		lock (_rootsLock)
		{
			// A root moved inside another folder by the IDE (e.g. an outside project moved into the solution folder) is now part of that tree, so is just dropped
			var roots = Roots.Where(root => root.Parent is null && rootPaths.Any(p => PathComparison.IsSameOrUnder(p, root.Path))).ToList();
			foreach (var staleRoot in Roots.Except(roots))
			{
				if (staleRoot.Parent is null) RemoveRoot(staleRoot);
				else AllFolders.Add(staleRoot); // Now an ordinary folder
			}
			foreach (var rootPath in rootPaths)
			{
				if (roots.Any(r => PathComparison.IsSameOrUnder(rootPath, r.Path))) continue;
				// A new project containing an existing root (e.g. a project above the solution, added after opening it) would need that root's files re-read,
				// replacing SharpIdeFiles that may be open. Rare enough that it waits until the solution is next opened - until then the project has no folder.
				if (roots.Any(r => PathComparison.IsSameOrUnder(r.Path, rootPath))) continue;
				roots.Add(AddRoot(rootPath));
			}
			Roots = [..roots];
		}
		RootsChanged?.Invoke();
	}

	/// The solution's folder - widened to the folder of a project containing it (its own files, which MSBuild globs anyway) - plus each project folder outside it.
	/// Nested project folders are covered by their outer one. Never a drive root (e.g. a project file at D:\), as that would mean enumerating and watching the whole drive.
	private static List<string> GetRootPaths(string solutionDirectory, IEnumerable<string> projectFilePaths)
	{
		var projectDirectories = projectFilePaths
			.Select(p => Path.GetDirectoryName(p)!)
			.Where(d => PathComparison.PathEquals(Path.GetPathRoot(d), d) is false && Directory.Exists(d))
			.Distinct(PathComparison.Comparer)
			.OrderBy(d => d.Length) // Outer folders first
			.ToList();
		var solutionRoot = projectDirectories.FirstOrDefault(d => PathComparison.IsSameOrUnder(solutionDirectory, d)) ?? solutionDirectory;
		List<string> rootPaths = [solutionRoot];
		foreach (var projectDirectory in projectDirectories)
		{
			if (rootPaths.Any(r => PathComparison.IsSameOrUnder(projectDirectory, r))) continue;
			rootPaths.Add(projectDirectory);
		}
		return rootPaths;
	}

	private SharpIdeRootFolder AddRoot(string path)
	{
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();
		var root = new SharpIdeRootFolder(new DirectoryInfo(path), allFiles, allFolders);
		foreach (var file in allFiles) AllFiles.TryAdd(file.Path, file);
		AllFolders.AddRange(allFolders);
		return root;
	}

	private void RemoveRoot(SharpIdeRootFolder root)
	{
		var stack = new Stack<SharpIdeFolder>([root]);
		while (stack.TryPop(out var folder))
		{
			foreach (var file in folder.Files) AllFiles.TryRemove(KeyValuePair.Create(file.Path, file));
			foreach (var subFolder in folder.Folders)
			{
				AllFolders.Remove(subFolder);
				stack.Push(subFolder);
			}
		}
	}
}
