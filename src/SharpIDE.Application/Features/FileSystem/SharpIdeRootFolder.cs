using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ObservableCollections;
using R3;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileSystem;

/// The top of one of a SharpIdeFileTree's trees - the solution's folder, or a project's folder outside it. Has no parent.
public class SharpIdeRootFolder : SharpIdeFolder
{
	[SetsRequiredMembers]
	internal SharpIdeRootFolder(DirectoryInfo folderInfo, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders)
	{
		Path = folderInfo.FullName;
		Name = new ReactiveProperty<string>(folderInfo.Name);
		Parent = null!;

		Files = new ObservableList<SharpIdeFile>(folderInfo.GetFiles(this, allFiles));
		Folders = new ObservableList<SharpIdeFolder>(this.GetSubFolders(this, allFiles, allFolders));
	}
}
