using System.Collections.Concurrent;
using SharpIDE.Application.Features.FileWatching;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public static class TreeMapperV2
{
	private static readonly string[] _excludedFolders = ["bin", "obj", "node_modules", ".vs", ".git", ".idea", ".vscode"];
	private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _excludedFoldersLookup = new HashSet<string>(_excludedFolders, StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

	public static bool IsExcludedFolderName(ReadOnlySpan<char> folderName) => _excludedFoldersLookup.Contains(folderName);

	/// True if any folder in the path below rootPath is excluded (e.g. bin, obj). Allocation free.
	public static bool IsInExcludedFolder(string rootPath, string fullPath)
	{
		if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) is false) return true; // Not ours
		var relativePath = fullPath.AsSpan(rootPath.Length);
		foreach (var segmentRange in relativePath.SplitAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
		{
			if (IsExcludedFolderName(relativePath[segmentRange])) return true;
		}
		return false;
	}
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
		try
		{
			var fileInfos = directoryInfo.EnumerateFiles().AsValueEnumerable();
			var sharpIdeFiles = fileInfos.Select(f => new SharpIdeFile(f.FullName, f.Name, f.Extension, parent, allFiles, diskStamp: FileStamp.From(f))).ToList();
			sharpIdeFiles.Sort(SharpIdeFileComparer.Instance);
			return sharpIdeFiles;
		}
		catch (UnauthorizedAccessException)
		{
			return [];
		}
	}
}
