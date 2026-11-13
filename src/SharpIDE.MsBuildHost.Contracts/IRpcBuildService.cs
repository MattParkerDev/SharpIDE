using PolyType;
using StreamJsonRpc;

namespace SharpIDE.MsBuildHost.Contracts;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IRpcBuildService
{
	Task<bool> Ping();
	Task<(BuildResultDto buildResultDto, Exception? Exception)> MsBuildAsync(string solutionOrProjectFilePath, BuildTypeDto buildType = BuildTypeDto.Build, CancellationToken cancellationToken = default);
	Task BeginWritingMsBuildOutputToSocket(string unixDomainSocketFilePath);
	Task<(string RuntimeVersion, string MsBuildSdkPath)> GetMsbuildInfoAsync();

	/// Restores, then builds the project with Build;ComputeRunArguments (TargetFramework set as a global property when given), returning the run properties from its state after the build
	Task<RunBuildResultDto> BuildProjectForRunAsync(string projectFilePath, string? targetFramework, CancellationToken cancellationToken = default);

	// Package references - these write project files, then restore, so they run as a build

	/// Adds the package to the project at the version, or moves it to the version, writing the version where the project's evaluation keeps it
	/// (its PackageReference, VersionOverride, Directory.Packages.props' PackageVersion, or a property the version names), then restores.
	/// Restore failing puts the files back.
	Task<PackageReferenceChangeResultDto> AddOrUpdatePackageReferenceAsync(string projectFilePath, string packageId, string version, CancellationToken cancellationToken = default);
	/// Removes the project's own PackageReference to the package, then restores. A central PackageVersion is left, as other projects may use it.
	Task<PackageReferenceChangeResultDto> RemovePackageReferenceAsync(string projectFilePath, string packageId, CancellationToken cancellationToken = default);
}
