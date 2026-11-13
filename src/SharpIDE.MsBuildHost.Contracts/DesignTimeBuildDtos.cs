namespace SharpIDE.MsBuildHost.Contracts;

// Copied from Roslyn's Microsoft.CodeAnalysis.Workspaces.MSBuild.Contracts (ProjectFileInfo, DocumentFileInfo, etc.), so the host and the IDE's project loader share types we own
// https://github.com/dotnet/roslyn/tree/main/src/Workspaces/MSBuild/Contracts

/// The result of design-time building one project file - one ProjectFileInfo per target framework (one if not multi-targeting)
public sealed record DesignTimeBuildResultDto(string ProjectFilePath, ProjectFileInfoDto[] ProjectFileInfos, DiagnosticLogItemDto[] Diagnostics);

/// A project loaded from disk and design-time built. If the project is multi-targeting, this is a single target framework.
public sealed record ProjectFileInfoDto
{
	public bool IsEmpty { get; init; }
	public required string Language { get; init; }
	public string? FilePath { get; init; }
	public string? OutputFilePath { get; init; }
	public string? OutputRefFilePath { get; init; }
	public string? IntermediateOutputFilePath { get; init; }
	public string? GeneratedFilesOutputDirectory { get; init; }
	/// "" if not defined, which means the global namespace, or null if unknown
	public string? DefaultNamespace { get; init; }
	/// The NuGet short name form, e.g. net10.0
	public string? TargetFramework { get; init; }
	public string? TargetFrameworkIdentifier { get; init; }
	/// The command line args used to compile the project
	public required string[] CommandLineArgs { get; init; }
	public required DocumentFileInfoDto[] Documents { get; init; }
	public required DocumentFileInfoDto[] AdditionalDocuments { get; init; }
	public required DocumentFileInfoDto[] AnalyzerConfigDocuments { get; init; }
	public required ProjectFileReferenceDto[] ProjectReferences { get; init; }
	public required string[] ProjectCapabilities { get; init; }
	public required string[] ContentFilePaths { get; init; }
	/// The path to project.assets.json in obj/
	public string? ProjectAssetsFilePath { get; init; }
	public required PackageReferenceItemDto[] PackageReferences { get; init; }
	/// Null to obtain them from CommandLineArgs. When non-null, used instead of any metadata references in the command line - set when the design-time build produced no compiler command line.
	public required MetadataReferenceItemDto[]? MetadataReferences { get; init; }
	/// Null to obtain them from CommandLineArgs. When non-null, used instead of any analyzer references in the command line.
	public required string[]? AnalyzerReferences { get; init; }
	/// Zero if not specified
	public int CodePage { get; init; }
	/// The value of the ChecksumAlgorithm or PdbChecksumAlgorithm property
	public string? ChecksumAlgorithm { get; init; }
	/// For .NET Framework projects
	public string? TargetFrameworkVersion { get; init; }
	public required FileGlobsDto[] FileGlobs { get; init; }

	public override string ToString() => string.IsNullOrWhiteSpace(TargetFramework) ? FilePath ?? string.Empty : $"{FilePath} ({TargetFramework})";

	public static ProjectFileInfoDto CreateEmpty(string language, string? filePath) => new()
	{
		IsEmpty = true,
		Language = language,
		FilePath = filePath,
		CommandLineArgs = [],
		Documents = [],
		AdditionalDocuments = [],
		AnalyzerConfigDocuments = [],
		ProjectReferences = [],
		PackageReferences = [],
		MetadataReferences = null,
		AnalyzerReferences = null,
		ProjectCapabilities = [],
		ContentFilePaths = [],
		FileGlobs = []
	};
}

/// A source file in a project. LogicalPath is relative to the project, and differs from FilePath for linked files.
public sealed record DocumentFileInfoDto(string FilePath, string LogicalPath, bool IsLinked, bool IsGenerated, string[] Folders);

/// Path may be relative to the referencing project file, or absolute
public sealed record ProjectFileReferenceDto(string Path, string[] Aliases, bool ReferenceOutputAssembly);

public sealed record PackageReferenceItemDto(string Name, string VersionRange);

public sealed record MetadataReferenceItemDto(string Path, string[] Aliases, bool EmbedInteropTypes);

public sealed record FileGlobsDto(string[] Includes, string[] Excludes, string[] Removes);

public enum DiagnosticLogItemKindDto { Error, Warning }

public sealed record DiagnosticLogItemDto(DiagnosticLogItemKindDto Kind, string Message, string ProjectFilePath);
