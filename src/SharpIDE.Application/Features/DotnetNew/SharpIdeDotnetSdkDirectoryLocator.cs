namespace SharpIDE.Application.Features.DotnetNew;

public static class SharpIdeDotnetSdkDirectoryLocator
{
	public static string? GetDotnetSdkDirectory()
	{
		// TODO: Use sdk path specified in global.json also
		var dotnetRootEnvVar = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		if (!string.IsNullOrWhiteSpace(dotnetRootEnvVar) && Directory.Exists(dotnetRootEnvVar))
		{
			return dotnetRootEnvVar;
		}
		// if we don't have DOTNET_ROOT, use the default paths
		var sdkDirectory = true switch
		{
			_ when OperatingSystem.IsWindows() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "sdk") + '\\',
			_ when OperatingSystem.IsLinux() => "/usr/share/dotnet/sdk/",
			// falling back to a default on linux is problematic, but in any scenario where the sdk is located in a non-default location, DOTNET_ROOT should be set.
			_ when OperatingSystem.IsMacOS() => "/usr/local/share/dotnet/sdk/",
			_ => null
		};
		return sdkDirectory;
	}
}
