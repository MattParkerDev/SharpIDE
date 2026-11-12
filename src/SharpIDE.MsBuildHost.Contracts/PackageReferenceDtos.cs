namespace SharpIDE.MsBuildHost.Contracts;

/// The result of adding, updating or removing a package reference. On failure, every file the change wrote has been put back as it was.
/// ChangedFiles are the project files (csproj, Directory.Packages.props, Directory.Build.props...) the change wrote, which are left written only on success.
/// Errors are MSBuild/NuGet's own errors from restore (e.g. NU1202 - the package doesn't support a target framework), if restore was the step that failed.
public sealed record PackageReferenceChangeResultDto(bool Success, string? Error, List<string> ChangedFiles, List<string> Errors);
