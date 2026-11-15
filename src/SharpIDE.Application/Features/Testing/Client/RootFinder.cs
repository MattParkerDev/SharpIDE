

namespace SharpIDE.Application.Features.Testing.Client;

/// <summary>
/// Provides functionality to locate the root directory of a Git repository.
/// </summary>
/// <remarks>The <see cref="RootFinder"/> class is used to find the root directory of a Git repository by
/// searching for a ".git" directory or file starting from the application's base directory and moving up the directory
/// hierarchy. This is useful for applications that need to determine the root of a project or repository.</remarks>
#if ROOT_FINDER_PUBLIC
public
#else
internal
#endif
	static class RootFinder
{
	private static string? s_root;

	/// <summary>
	/// Finds the root directory of a Git repository starting from the application's base directory.
	/// </summary>
	/// <remarks>This method searches for a ".git" directory or file in the application's base directory and
	/// its parent directories. If a Git repository is found, the path to its root directory is returned. If no Git
	/// repository is found, an <see cref="InvalidOperationException"/> is thrown.</remarks>
	/// <returns>The path to the root directory of the Git repository, ending with a directory separator character.</returns>
	/// <exception cref="InvalidOperationException">Thrown if a Git repository is not found in the application's base directory or any of its parent directories.</exception>
	public static string Find()
	{
		if (s_root != null)
		{
			return s_root;
		}

		string path = AppContext.BaseDirectory;
		string currentDirectory = path;
		string rootDriveDirectory = Directory.GetDirectoryRoot(currentDirectory);
		while (rootDriveDirectory != currentDirectory)
		{
			string gitPath = Path.Combine(currentDirectory, ".git");

			// When working with git worktrees, the .git is a file not a folder
			if (Directory.Exists(gitPath) || File.Exists(gitPath))
			{
				s_root = currentDirectory + Path.DirectorySeparatorChar;
				return s_root;
			}

			currentDirectory = Directory.GetParent(currentDirectory)!.ToString();
		}

		throw new InvalidOperationException($"Could not find solution root, .git not found in {path} or any parent directory.");
	}
}
