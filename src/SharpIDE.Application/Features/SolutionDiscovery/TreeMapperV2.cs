using System.Collections.Concurrent;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public static class TreeMapperV2
{
	public static IEnumerable<SharpIdeFile> GetAllFiles(this SharpIdeFolder folder)
	{
		return folder.Files
			.Concat(folder.Folders.SelectMany(sub => sub.GetAllFiles()));
	}
	public static List<SharpIdeFolder> GetSubFolders(string csprojectPath, SharpIdeProjectModel sharpIdeProjectModel, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders)
	{
		var projectDirectory = Path.GetDirectoryName(csprojectPath)!;
		var rootFolder = new SharpIdeFolder
		{
			Parent = sharpIdeProjectModel,
			Path = projectDirectory,
			Name = null!,
			Files = [],
			Folders = []
		};
		var subFolders = rootFolder.GetSubFolders(sharpIdeProjectModel, allFiles, allFolders);
		return subFolders;
	}

	private static readonly string[] _excludedFolders = ["bin", "obj", "node_modules"];
	public static List<SharpIdeFolder> GetSubFolders(this SharpIdeFolder folder, IExpandableSharpIdeNode parent, ConcurrentBag<SharpIdeFile> allFiles, ConcurrentBag<SharpIdeFolder> allFolders)
	{
		var directoryInfo = new DirectoryInfo(folder.Path);
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

	public static List<SharpIdeFile> GetFiles(string csprojectPath, SharpIdeProjectModel sharpIdeProjectModel, ConcurrentBag<SharpIdeFile> allFiles)
	{
		var projectDirectory = Path.GetDirectoryName(csprojectPath)!;
		var directoryInfo = new DirectoryInfo(projectDirectory);
		return GetFiles(directoryInfo, sharpIdeProjectModel, allFiles);
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
