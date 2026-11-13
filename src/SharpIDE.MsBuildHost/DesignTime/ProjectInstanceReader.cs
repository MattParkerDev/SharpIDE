using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost.DesignTime;

// Adapted from Roslyn's BuildHost ProjectInstanceReader
// https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/BuildHost/MSBuild/ProjectFile/ProjectInstanceReader.cs

/// Reads a design-time built project into a ProjectFileInfoDto
internal readonly struct ProjectInstanceReader
{
	/// Everything this reads from a built project (CSharpCommandLineArgumentReader and ProjectInstanceExtensions included) - so a build returns just that, rather than the project's whole state,
	/// which for a build on an out of proc node is serialized back over its pipe. Anything read that isn't listed here is missing (an empty property, no items, no metadata).
	public static readonly RequestedProjectState RequestedProjectState = new()
	{
		PropertyFilters =
		[
			// ProjectInstanceReader
			"ChecksumAlgorithm", "PdbChecksumAlgorithm", "TargetFramework", "TargetPath", "TargetRefPath", "CompilerGeneratedFilesOutputPath", "RootNamespace",
			"TargetFrameworkIdentifier", "TargetFrameworkVersion", "ProjectAssetsFile", "CodePage",
			// CSharpCommandLineArgumentReader
			"AllowUnsafeBlocks", "AppConfigForCompiler", "BaseAddress", "CheckForOverflowUnderflow", "DebugSymbols", "DebugType", "DefineConstants", "DelaySign", "ErrorReport",
			"Features", "FileAlignment", "GenerateFullPaths", "HighEntropyVA", "KeyContainerName", "KeyOriginatorFile", "LangVersion", "ModuleAssemblyName", "NoCompilerStandardLib",
			"NoWarn", "Optimize", "OutputType", "PdbFile", "PlatformTarget", "ResolvedCodeAnalysisRuleSet", "SignAssembly", "StartupObject", "SubsystemVersion",
			"TreatWarningsAsErrors", "WarningLevel", "WarningsAsErrors", "WarningsNotAsErrors",
		],
		// Item type -> the metadata read from its items
		ItemFilters = new Dictionary<string, List<string>>
		{
			["CscCommandLineArgs"] = [],
			["Compile"] = ["Link"],
			["AdditionalFiles"] = ["Link"],
			["EditorConfigFiles"] = ["Link"],
			["ReferencePath"] = ["ReferenceOutputAssembly", "Aliases", "EmbedInteropTypes"],
			["Analyzer"] = [],
			["ProjectReference"] = ["ReferenceOutputAssembly", "Aliases"],
			["PackageReference"] = ["Version"],
			["IntermediateAssembly"] = [],
			["ProjectCapability"] = [],
			["Content"] = [],
			["Import"] = [],
			["DocFileItem"] = [],
		},
	};

	private readonly string _language;
	private readonly ProjectInstance _projectInstance;
	private readonly Project _project;
	private readonly string _projectFullPath;
	private readonly string _projectDirectory;

	/// project is the evaluation projectInstance was built from - ProjectStateAfterBuild (from an out of proc node) has no FullPath, and only the evaluation has globs
	public ProjectInstanceReader(string language, ProjectInstance projectInstance, Project project)
	{
		_language = language;
		_projectInstance = projectInstance;
		_project = project;
		_projectFullPath = project.FullPath;
		_projectDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(_projectFullPath)!) + Path.DirectorySeparatorChar;
	}

	public ProjectFileInfoDto CreateProjectFileInfo()
	{
		var commandLineArgs = _language is LanguageNames.CSharp
			? _projectInstance.GetItems("CscCommandLineArgs").Select(item => item.EvaluatedInclude).ToArray()
			: [];
		MetadataReferenceItemDto[]? metadataReferences = null;
		string[]? analyzerReferences = null;

		// If the compiler didn't provide arguments (the design-time build failed), pass references separately, and recover the remaining options from the evaluated project
		if (commandLineArgs.Length is 0)
		{
			metadataReferences = [.. _projectInstance.GetMetadataReferences(_projectDirectory)];
			analyzerReferences = [.. _projectInstance.GetAnalyzers().Select(static item => item.ItemSpec).Select(GetAbsolutePathRelativeToProject)];
			commandLineArgs = _language is LanguageNames.CSharp ? CSharpCommandLineArgumentReader.Read(_projectInstance, _projectDirectory) : [];
		}

		var checksumAlgorithm = _projectInstance.ReadPropertyString("ChecksumAlgorithm");
		// F# uses PdbChecksumAlgorithm
		if (string.IsNullOrEmpty(checksumAlgorithm)) checksumAlgorithm = _projectInstance.ReadPropertyString("PdbChecksumAlgorithm");

		var targetFramework = _projectInstance.ReadPropertyString("TargetFramework");

		return new ProjectFileInfoDto
		{
			Language = _language,
			FilePath = _projectFullPath,
			OutputFilePath = GetAbsolutePathOrNull(_projectInstance.ReadPropertyString("TargetPath")),
			OutputRefFilePath = GetAbsolutePathOrNull(_projectInstance.ReadPropertyString("TargetRefPath")),
			GeneratedFilesOutputDirectory = GetAbsolutePathOrNull(_projectInstance.ReadPropertyString("CompilerGeneratedFilesOutputPath")),
			IntermediateOutputFilePath = GetAbsolutePathOrNull(_projectInstance.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude),
			DefaultNamespace = _projectInstance.ReadPropertyString("RootNamespace") ?? string.Empty,
			TargetFramework = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework,
			TargetFrameworkIdentifier = _projectInstance.ReadPropertyString("TargetFrameworkIdentifier"),
			TargetFrameworkVersion = _projectInstance.ReadPropertyString("TargetFrameworkVersion"),
			ProjectAssetsFilePath = _projectInstance.ReadPropertyString("ProjectAssetsFile"),
			CommandLineArgs = commandLineArgs,
			Documents = [.. _projectInstance.GetDocuments().Where(IsNotTemporaryGeneratedFile).Select(MakeDocumentFileInfo)],
			AdditionalDocuments = [.. _projectInstance.GetAdditionalFiles().Select(MakeDocumentFileInfo)],
			AnalyzerConfigDocuments = [.. _projectInstance.GetEditorConfigFiles().Select(MakeDocumentFileInfo)],
			ProjectReferences = [.. _projectInstance.GetProjectReferences()],
			PackageReferences = _projectInstance.GetPackageReferences(),
			MetadataReferences = metadataReferences,
			AnalyzerReferences = analyzerReferences,
			CodePage = _projectInstance.ReadCodePage(),
			ChecksumAlgorithm = checksumAlgorithm,
			ProjectCapabilities = [.. _projectInstance.GetItems("ProjectCapability").Select(item => item.EvaluatedInclude)],
			ContentFilePaths = [.. _projectInstance.GetItems("Content").Select(static item => item.EvaluatedInclude).Select(GetAbsolutePathRelativeToProject)],
			FileGlobs = [.. _project.GetAllGlobs().Select(static g => new FileGlobsDto(
				Includes: [.. g.IncludeGlobs.Select(ExpandAbsolutePath)],
				Excludes: [.. g.Excludes.Select(ExpandAbsolutePath)],
				Removes: [.. g.Removes.Select(ExpandAbsolutePath)]))]
		};
	}

	/// Normalises `..` etc. in an absolute path (which may contain glob wildcards), leaving a relative one as it is
	private static string ExpandAbsolutePath(string path) => Path.IsPathFullyQualified(path) ? ProjectInstanceExtensions.ResolvePath(path, Path.GetPathRoot(path)!) : path;

	private static bool IsNotTemporaryGeneratedFile(ITaskItem item) => Path.GetFileName(item.ItemSpec).StartsWith("TemporaryGeneratedFile_", StringComparison.Ordinal) is false;

	private DocumentFileInfoDto MakeDocumentFileInfo(ITaskItem documentItem)
	{
		var link = documentItem.GetMetadata("Link");
		return new DocumentFileInfoDto(
			FilePath: GetAbsolutePathRelativeToProject(documentItem.ItemSpec),
			LogicalPath: GetDocumentLogicalPath(documentItem.ItemSpec, link),
			IsLinked: string.IsNullOrEmpty(link) is false,
			IsGenerated: false,
			Folders: GetRelativeFolders(documentItem.ItemSpec, link));
	}

	private string[] GetRelativeFolders(string itemSpec, string link)
	{
		if (string.IsNullOrEmpty(link) is false)
		{
			return (Path.GetDirectoryName(link) ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		// itemSpec made absolute first - Path.GetRelativePath would resolve a relative one against the current directory, which MSBuild changes as it builds projects
		var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(_projectDirectory, GetAbsolutePathRelativeToProject(itemSpec)));
		return relativeDirectory is null ? [] : relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
	}

	private string GetDocumentLogicalPath(string itemSpec, string link)
	{
		// A Link in the project file forms the logical path
		if (string.IsNullOrEmpty(link) is false) return link;
		if (Path.IsPathFullyQualified(itemSpec) is false) return itemSpec;

		var normalizedPath = ProjectInstanceExtensions.ResolvePath(itemSpec, _projectDirectory);
		// Within the project directory (or below) - relative to it. Otherwise logically at the root of the project (if several end up with the same logical name, so be it).
		return normalizedPath.StartsWith(_projectDirectory, StringComparison.OrdinalIgnoreCase)
			? normalizedPath[_projectDirectory.Length..]
			: Path.GetFileName(normalizedPath);
	}

	/// Absolute, but might not be normalised
	private string GetAbsolutePathRelativeToProject(string path) => ProjectInstanceExtensions.ResolvePath(path, _projectDirectory);

	private string? GetAbsolutePathOrNull(string? path) => string.IsNullOrWhiteSpace(path) ? null : GetAbsolutePathRelativeToProject(path);
}

internal static class LanguageNames
{
	public const string CSharp = "C#";
	public const string VisualBasic = "Visual Basic";
	public const string FSharp = "F#";
}
