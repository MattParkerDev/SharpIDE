using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileSystem;

public class SharpIdeRootFolder : SharpIdeFolder
{
	public ConcurrentDictionary<string, SharpIdeFile> AllFiles { get; }
	public ObservableList<SharpIdeFolder> AllFolders { get; }

	[SetsRequiredMembers]
	public SharpIdeRootFolder(DirectoryInfo folderInfo)
	{
		var allFiles = new ConcurrentBag<SharpIdeFile>();
		var allFolders = new ConcurrentBag<SharpIdeFolder>();

		Path = folderInfo.FullName;
		Name = new ReactiveProperty<string>(folderInfo.Name);
		Parent = null!;

		Files = new ObservableList<SharpIdeFile>(folderInfo.GetFiles(this, allFiles));
		Folders = new ObservableList<SharpIdeFolder>(this.GetSubFolders(this, allFiles, allFolders));

		AllFiles = new ConcurrentDictionary<string, SharpIdeFile>(allFiles.DistinctBy(s => s.Path, PathComparison.Comparer).Select(s => KeyValuePair.Create(s.Path, s)), PathComparison.Comparer);
		AllFolders = new ObservableList<SharpIdeFolder>(allFolders);
	}

	/// Returns the SharpIdeFolder that directly contains the given .csproj file.
	/// If the .csproj is at the solution root, returns this SharpIdeRootFolder itself.
	public SharpIdeFolder? GetFolderForProject(string csprojFullPath)
	{
		var csprojFile = AllFiles.GetValueOrDefault(csprojFullPath);
		if (csprojFile is null) return null;
		return csprojFile.Parent as SharpIdeFolder ?? throw new InvalidOperationException($"Parent of '{csprojFullPath}' is not a SharpIdeFolder.");
	}
}
