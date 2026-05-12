using System.Collections.Concurrent;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public static class TreeMapperV2
{
	private static readonly string[] _excludedFolders = ["bin", "obj", "node_modules", ".vs", ".git", ".idea", ".vscode"];
	public static List<SharpIdeFolder> GetSubFolders(this SharpIdeFolder folder, IExpandableSharpIdeNode parent, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders) => GetSubFolders(folder.Path, parent, allFiles, allFolders);
	public static List<SharpIdeFolder> GetSubFolders(string folderPath, IExpandableSharpIdeNode parent, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders)
	{
		var directoryInfo = new DirectoryInfo(folderPath);
		ConcurrentBag<SharpIdeFolder> subFolders = [];

		List<DirectoryInfo> subFolderInfos;
		try
		{
			subFolderInfos = directoryInfo.EnumerateDirectories("*", new EnumerationOptions
			{
				IgnoreInaccessible = false,
				AttributesToSkip = FileAttributes.ReparsePoint
			}).Where(s => _excludedFolders.Contains(s.Name, StringComparer.InvariantCultureIgnoreCase) is false).ToList();
		}
		catch (UnauthorizedAccessException)
		{
			return subFolders.ToList();
		}

		Parallel.ForEach(subFolderInfos, subFolderInfo =>
		{
			var subFolder = new SharpIdeFolder(subFolderInfo, parent, allFiles, allFolders);
			subFolders.Add(subFolder);
			allFolders.Add(subFolder);
		});

		var sharpIdeFolders = subFolders.ToList();
		sharpIdeFolders.Sort(SharpIdeFolderComparer.Instance);
		return sharpIdeFolders;
	}

	public static List<SharpIdeFile> GetFiles(this DirectoryInfo directoryInfo, IExpandableSharpIdeNode parent, ConcurrentBag<SharpIdeFile> allFiles)
	{
		List<FileInfo> fileInfos;
		try
		{
			fileInfos = directoryInfo.EnumerateFiles().ToList();
		}
		catch (UnauthorizedAccessException)
		{
			return [];
		}

		var sharpIdeFiles = fileInfos.Select(f => new SharpIdeFile(f.FullName, f.Name, f.Extension, parent, allFiles)).ToList();
		sharpIdeFiles.Sort(SharpIdeFileComparer.Instance);
		return sharpIdeFiles;
	}
}
