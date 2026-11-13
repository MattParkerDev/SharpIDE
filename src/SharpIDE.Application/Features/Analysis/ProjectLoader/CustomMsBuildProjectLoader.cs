using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.MSBuild;
using Roslyn.Utilities;
using SharpIDE.Application.Features.Build;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.Application.Features.Analysis.ProjectLoader;
// Adapted from Roslyn's MSBuildProjectLoader - https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/Core/MSBuild/MSBuildProjectLoader.cs
// - Analyzers are loaded with IAnalyzerAssemblyLoaderProvider rather than IAnalyzerService, so they are shadow copied, to prevent locking.
//   (My attempts to provide a custom IAnalyzerService to the MEF composition were in vain.)
// - Projects are design-time built by our own long-lived design-time host (DesignTimeBuildService), rather than a Roslyn BuildHost started for each load.
// see also roslyn LanguageServerProjectLoader, LanguageServerProjectSystem, ProjectSystemProjectFactory - a more incremental approach
public partial class CustomMsBuildProjectLoader
{
	// the services for the projects and solutions are intended to be loaded into.
	private readonly SolutionServices _solutionServices;
	private readonly DesignTimeBuildService _designTimeBuildService;

	private readonly DiagnosticReporter _diagnosticReporter;
	private readonly PathResolver _pathResolver;
	private readonly ProjectFileExtensionRegistry _projectFileExtensionRegistry;

	// used to protect access to the following mutable state
	private readonly NonReentrantLock _dataGuard = new();
	private string? _solutionFilePath;

	/// <param name="properties">An optional dictionary of additional MSBuild properties and values to use when loading projects.
	/// These are the same properties that are passed to MSBuild via the /property:&lt;n&gt;=&lt;v&gt; command line argument.</param>
	public CustomMsBuildProjectLoader(Workspace workspace, DesignTimeBuildService designTimeBuildService, ImmutableDictionary<string, string>? properties = null)
	{
		_solutionServices = workspace.Services.SolutionServices;
		_designTimeBuildService = designTimeBuildService;
		_diagnosticReporter = new DiagnosticReporter(workspace);
		_pathResolver = new PathResolver(_diagnosticReporter);
		_projectFileExtensionRegistry = new ProjectFileExtensionRegistry(_diagnosticReporter, _solutionServices.GetService<Microsoft.CodeAnalysis.FileBasedPrograms.IFileBasedProgramService>());

		Properties = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

		if (properties != null)
		{
			Properties = Properties.AddRange(properties);
		}
	}

	/// <summary>
	/// The MSBuild properties used when interpreting project files.
	/// These are the same properties that are passed to MSBuild via the /property:&lt;n&gt;=&lt;v&gt; command line argument.
	/// </summary>
	public ImmutableDictionary<string, string> Properties { get; private set; }

	/// <summary>
	/// Determines if metadata from existing output assemblies is loaded instead of opening referenced projects.
	/// If the referenced project is already opened, the metadata will not be loaded.
	/// If the metadata assembly cannot be found the referenced project will be opened instead.
	/// </summary>
	public bool LoadMetadataForReferencedProjects { get; set; } = false;

	/// <summary>
	/// Determines if unrecognized projects are skipped when solutions or projects are opened.
	///
	/// A project is unrecognized if it either has
	///   a) an invalid file path,
	///   b) a non-existent project file,
	///   c) has an unrecognized file extension or
	///   d) a file extension associated with an unsupported language.
	///
	/// If unrecognized projects cannot be skipped a corresponding exception is thrown.
	/// </summary>
	public bool SkipUnrecognizedProjects { get; set; } = true;

	/// <summary>
	/// Associates a project file extension with a language name.
	/// </summary>
	public void AssociateFileExtensionWithLanguage(string projectFileExtension, string language)
	{
		ArgumentNullException.ThrowIfNull(projectFileExtension);
		ArgumentNullException.ThrowIfNull(language);
		_projectFileExtensionRegistry.AssociateFileExtensionWithLanguage(projectFileExtension, language);
	}

	private void SetSolutionProperties(string? solutionFilePath)
	{
		const string SolutionDirProperty = "SolutionDir";

		// When MSBuild is building an individual project, it doesn't define $(SolutionDir).
		// However when building an .sln file, or when working inside Visual Studio,
		// $(SolutionDir) is defined to be the directory where the .sln file is located.
		// Some projects out there rely on $(SolutionDir) being set (although the best practice is to
		// use MSBuildProjectDirectory which is always defined).
		if (!RoslynString.IsNullOrEmpty(solutionFilePath))
		{
			var solutionDirectory = PathUtilities.GetDirectoryName(solutionFilePath) + PathUtilities.DirectorySeparatorChar;

			if (Directory.Exists(solutionDirectory))
			{
				Properties = Properties.SetItem(SolutionDirProperty, solutionDirectory);
			}
		}
	}

	private DiagnosticReportingMode GetReportingModeForUnrecognizedProjects()
		=> this.SkipUnrecognizedProjects
			? DiagnosticReportingMode.Log
			: DiagnosticReportingMode.Throw;

	/// <summary>
	/// Loads the <see cref="SolutionInfo"/> for the specified solution file, including all projects referenced by the solution file and
	/// all the projects referenced by the project files.
	/// </summary>
	/// <param name="solutionFilePath">The path to the solution file to be loaded. This may be an absolute path or a path relative to the
	/// current working directory.</param>
	/// <param name="cancellationToken">An optional <see cref="CancellationToken"/> to allow cancellation of this operation.</param>
	public async Task<(SolutionInfo, Dictionary<ProjectId, ProjectFileInfoDto> projectFileInfos)> LoadSolutionInfoAsync(
		string solutionFilePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(solutionFilePath);

		var reportingMode = GetReportingModeForUnrecognizedProjects();
		var reportingOptions = new DiagnosticReportingOptions(
			onPathFailure: reportingMode,
			onLoaderFailure: reportingMode);

		var (absoluteSolutionPath, projects) = await SolutionFileReader.ReadSolutionFileAsync(solutionFilePath, _pathResolver, reportingMode, cancellationToken).ConfigureAwait(false);

		// TryGetAbsoluteSolutionPath should not return an invalid path
		var solutionDir = Path.GetDirectoryName(absoluteSolutionPath)!;

		var projectPaths =
			from project in projects
			let fullPath = _pathResolver.TryGetAbsoluteProjectPath(project.ProjectPath, solutionDir, reportingMode, out var absoluteProjectPath) ? absoluteProjectPath : null
			where fullPath != null
			select fullPath;

		using (_dataGuard.DisposableWait(cancellationToken))
		{
			_solutionFilePath = absoluteSolutionPath;
			SetSolutionProperties(absoluteSolutionPath);
		}

		var (projectInfos, projectFileInfos) = await LoadInfosAsync(
			[.. projectPaths],
			projectMap: null,
			progress: null,
			requestedProjectOptions: reportingOptions,
			discoveredProjectOptions: reportingOptions,
			preferMetadataForReferencesOfDiscoveredProjects: false,
			cancellationToken).ConfigureAwait(false);

		// construct workspace from loaded project infos
		return (SolutionInfo.Create(
			SolutionId.CreateNewId(debugName: absoluteSolutionPath),
			version: default,
			absoluteSolutionPath,
			projectInfos), projectFileInfos);
	}

	/// Loads the projects (and the projects they reference). Call LoadSolutionInfoAsync first, for the solution these projects are in.
	internal Task<(ImmutableArray<ProjectInfo> ProjectInfos, Dictionary<ProjectId, ProjectFileInfoDto> ProjectFileInfos)> LoadProjectInfosAsync(
		ImmutableArray<string> projectFilePaths,
		DesignTimeProjectMap? projectMap,
		IProgress<ProjectLoadProgress>? progress,
		CancellationToken cancellationToken)
	{
		Debug.Assert(projectFilePaths.All(PathUtilities.IsAbsolute));

		var reportingMode = GetReportingModeForUnrecognizedProjects();
		var discoveredProjectOptions = new DiagnosticReportingOptions(
			onPathFailure: reportingMode,
			onLoaderFailure: reportingMode);

		return LoadInfosAsync(
			projectFilePaths,
			projectMap,
			progress,
			requestedProjectOptions: DiagnosticReportingOptions.ThrowForAll,
			discoveredProjectOptions,
			LoadMetadataForReferencedProjects,
			cancellationToken);
	}

	private async Task<(ImmutableArray<ProjectInfo> ProjectInfos, Dictionary<ProjectId, ProjectFileInfoDto> ProjectFileInfos)> LoadInfosAsync(
		ImmutableArray<string> projectPaths,
		DesignTimeProjectMap? projectMap,
		IProgress<ProjectLoadProgress>? progress,
		DiagnosticReportingOptions requestedProjectOptions,
		DiagnosticReportingOptions discoveredProjectOptions,
		bool preferMetadataForReferencesOfDiscoveredProjects,
		CancellationToken cancellationToken)
	{
		var solutionFilePath = _solutionFilePath ?? throw new InvalidOperationException($"{nameof(LoadSolutionInfoAsync)} must be called first, for the solution the projects are in.");
		var projectFileInfoProvider = new DesignTimeBuildProjectFileInfoProvider(
			_designTimeBuildService,
			solutionFilePath,
			Properties,
			_projectFileExtensionRegistry,
			_diagnosticReporter,
			projectMap);

		// The Worker asks for one project at a time - so build all the requested ones up front, together. Ones they reference that aren't requested (e.g. outside the solution) are built as the Worker finds them.
		await projectFileInfoProvider.DesignTimeBuildAsync(projectPaths, cancellationToken).ConfigureAwait(false);

		var worker = new Worker(
			_solutionServices,
			_diagnosticReporter,
			_pathResolver,
			_projectFileExtensionRegistry,
			projectFileInfoProvider,
			projectPaths,
			projectMap,
			progress,
			requestedProjectOptions,
			discoveredProjectOptions,
			preferMetadataForReferencesOfDiscoveredProjects);

		var projectInfos = await worker.LoadAsync(cancellationToken).ConfigureAwait(false);
		return (projectInfos, worker.ProjectFileInfos);
	}
}
