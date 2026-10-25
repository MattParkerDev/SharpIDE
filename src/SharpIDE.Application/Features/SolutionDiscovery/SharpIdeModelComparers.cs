namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeFileComparer : IComparer<SharpIdeFile>
{
	public static readonly SharpIdeFileComparer Instance = new SharpIdeFileComparer();
	public int Compare(SharpIdeFile? x, SharpIdeFile? y)
	{
		if (ReferenceEquals(x, y)) return 0;
		if (x is null) return -1;
		if (y is null) return 1;

		int result = string.Compare(x.Name.Value, y.Name.Value, StringComparison.OrdinalIgnoreCase);

		return result;
	}
}

public class SharpIdeSolutionItemFileComparer : IComparer<SharpIdeSolutionItemFile>
{
	public static readonly SharpIdeSolutionItemFileComparer Instance = new SharpIdeSolutionItemFileComparer();
	public int Compare(SharpIdeSolutionItemFile? x, SharpIdeSolutionItemFile? y)
	{
		if (ReferenceEquals(x, y)) return 0;
		if (x is null) return -1;
		if (y is null) return 1;

		int result = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

		return result;
	}
}

public class SharpIdeSolutionFolderComparer : IComparer<SharpIdeSolutionFolder>
{
	public static readonly SharpIdeSolutionFolderComparer Instance = new SharpIdeSolutionFolderComparer();
	public int Compare(SharpIdeSolutionFolder? x, SharpIdeSolutionFolder? y)
	{
		if (ReferenceEquals(x, y)) return 0;
		if (x is null) return -1;
		if (y is null) return 1;

		int result = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

		return result;
	}
}

public class SharpIdeProjectComparer : IComparer<SharpIdeProjectModel>
{
	public static readonly SharpIdeProjectComparer Instance = new SharpIdeProjectComparer();
	public int Compare(SharpIdeProjectModel? x, SharpIdeProjectModel? y)
	{
		if (ReferenceEquals(x, y)) return 0;
		if (x is null) return -1;
		if (y is null) return 1;

		int result = string.Compare(x.Name.Value, y.Name.Value, StringComparison.OrdinalIgnoreCase);

		return result;
	}
}

// AI
public class SharpIdeFolderComparer : IComparer<SharpIdeFolder>
{
	public static readonly SharpIdeFolderComparer Instance = new SharpIdeFolderComparer();
	public int Compare(SharpIdeFolder? x, SharpIdeFolder? y)
	{
		if (ReferenceEquals(x, y)) return 0;
		if (x is null) return -1;
		if (y is null) return 1;

		// Special folders priority: Properties > wwwroot > others
		int xPriority = GetFolderPriority(x.Name.Value);
		int yPriority = GetFolderPriority(y.Name.Value);

		int priorityComparison = xPriority.CompareTo(yPriority);
		if (priorityComparison != 0) return priorityComparison;

		// Default alphabetical compare for same priority
		return string.Compare(x.Name.Value, y.Name.Value, StringComparison.OrdinalIgnoreCase);
	}

	private static int GetFolderPriority(string? name)
	{
		if (string.Equals(name, "Properties", StringComparison.OrdinalIgnoreCase))
			return 0;
		if (string.Equals(name, "wwwroot", StringComparison.OrdinalIgnoreCase))
			return 1;

		return 2; // all others
	}
}
