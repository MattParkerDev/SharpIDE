using System.Collections.Concurrent;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public static class TreeMapperV2
{
	public static IEnumerable<SharpIdeFile> GetAllFiles(this SharpIdeFolder folder)
	{
		return folder.Files
			.Concat(folder.Folders.SelectMany(sub => sub.GetAllFiles()));
	}
	public static List<SharpIdeFolder> GetSubFolders(string csprojectPath)
	{
		var projectDirectory = Path.GetDirectoryName(csprojectPath)!;
		var rootFolder = new SharpIdeFolder
		{
			Path = projectDirectory,
			Name = null!,
			Files = [],
			Folders = []
		};
		var subFolders = rootFolder.GetSubFolders();
		return subFolders;
	}

	private static readonly string[] _excludedFolders = ["bin", "obj", "node_modules"];
	public static List<SharpIdeFolder> GetSubFolders(this SharpIdeFolder folder)
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
			var subFolder = new SharpIdeFolder
			{
				Path = subFolderInfo.FullName,
				Name = subFolderInfo.Name,
				Files = GetFiles(subFolderInfo),
				Folders = new SharpIdeFolder
				{
					Path = subFolderInfo.FullName,
					Name = subFolderInfo.Name,
					Files = [],
					Folders = []
				}.GetSubFolders()
			};

			subFolders.Add(subFolder);
		});

		return subFolders.ToList();
	}

	public static List<SharpIdeFile> GetFiles(string csprojectPath)
	{
		var projectDirectory = Path.GetDirectoryName(csprojectPath)!;
		var directoryInfo = new DirectoryInfo(projectDirectory);
		return GetFiles(directoryInfo);
	}
	public static List<SharpIdeFile> GetFiles(DirectoryInfo directoryInfo)
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

		return fileInfos.Select(f => new SharpIdeFile
		{
			Path = f.FullName,
			Name = f.Name
		}).ToList();
	}
}
