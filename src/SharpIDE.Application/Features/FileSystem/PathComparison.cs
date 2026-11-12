namespace SharpIDE.Application.Features.FileSystem;

/// How file paths are compared - case insensitively on Windows and macOS (whose file systems are by default), case sensitively elsewhere.
/// A path's casing can come from the disk, the solution file, a csproj or MSBuild, and they don't always agree.
public static class PathComparison
{
	public static bool IsCaseInsensitive { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
	public static StringComparer Comparer { get; } = IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	public static StringComparison Comparison { get; } = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	public static bool PathEquals(string? a, string? b) => string.Equals(a, b, Comparison);

	/// True if path is folderPath, or inside it
	public static bool IsSameOrUnder(string path, string folderPath)
	{
		var folder = Path.TrimEndingDirectorySeparator(folderPath);
		if (path.StartsWith(folder, Comparison) is false) return false;
		if (path.Length == folder.Length) return true;
		// A drive root (e.g. C:\) keeps its trailing separator
		if (folder[^1] == Path.DirectorySeparatorChar || folder[^1] == Path.AltDirectorySeparatorChar) return true;
		return path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar;
	}
}
