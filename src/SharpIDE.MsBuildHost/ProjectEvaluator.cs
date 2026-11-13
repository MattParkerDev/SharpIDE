using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using SharpIDE.MsBuildHost.Contracts;
using SharpIDE.MsBuildHost.DesignTime;

namespace SharpIDE.MsBuildHost;

/// Evaluation only - never runs targets, and never touches BuildManager, so it can run alongside design-time builds in the design-time host.
/// (If a target ever needs running for evaluation, don't use ProjectInstance.Build - it opens a BuildManager.DefaultBuildManager session, which throws if a design-time batch already has one open.
/// Send it through DesignTimeBuildManager instead.)
/// Its ProjectCollection is its own (no global properties - evaluated as a real build of the project would be), but shares the process-wide XML cache with DesignTimeBuildManager's.
/// That cache's ProjectRootElements may be in use by a design-time evaluation, so they are never changed in place (no Reload, no edits) - the cache re-reads a file that changed
/// on disk (checked by timestamp) as a new ProjectRootElement, so a project whose own file changed is loaded again, and one whose imports changed is re-evaluated.
public sealed class ProjectEvaluator
{
	private const string TargetFrameworkProperty = "TargetFramework";

	private static readonly string[] PropertyNames =
	[
		"TargetFramework",
		"TargetFrameworks",
		"OutputType",
		"RootNamespace",
		"RunCommand",
		"RunArguments",
		"RunWorkingDirectory",
		"TargetPath",
		"OutDir",
		"ProjectAssetsFile",
		"IsTestingPlatformApplication",
		"UserSecretsId",
	];

	// Lives as long as the host. reuseProjectRootElementCache: the process-wide XML cache, which re-reads a file when it changes on disk (checked by timestamp)
	private readonly ProjectCollection _projectCollection = new(
		globalProperties: null,
		loggers: [],
		remoteLoggers: [],
		ToolsetDefinitionLocations.Default,
		maxNodeCount: 1,
		onlyLogCriticalEvents: false,
		loadProjectsReadOnly: false,
		useAsynchronousLogging: false,
		reuseProjectRootElementCache: true);
	// One batch at a time. Projects within a batch are evaluated in parallel.
	private readonly SemaphoreSlim _lock = new(1, 1);

	public async Task<List<ProjectEvaluationDto>> EvaluateProjectsAsync(List<string> projectFilePaths, CancellationToken cancellationToken)
	{
		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await EvaluateInParallelAsync(projectFilePaths, reloadProjectFile: _ => true, forceReevaluation: false, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task<List<ProjectEvaluationDto>> ReloadProjectsAsync(List<string> changedFilePaths, CancellationToken cancellationToken)
	{
		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var loadedProjects = _projectCollection.LoadedProjects.ToList();
			var outerProjectPaths = loadedProjects
				.Where(p => p.GlobalProperties.ContainsKey(TargetFrameworkProperty) is false)
				.Select(p => p.FullPath)
				.Distinct(PathComparer)
				.ToList();
			if (outerProjectPaths.Count is 0) return [];

			var changedPaths = changedFilePaths.Select(Path.GetFullPath).ToHashSet(PathComparer);
			var affectedProjectPaths = new HashSet<string>(PathComparer);
			foreach (var changedFilePath in changedPaths)
			{
				var importingProjects = loadedProjects
					.Where(p => PathComparer.Equals(p.FullPath, changedFilePath) || p.Imports.Any(i => PathComparer.Equals(i.ImportedProject.FullPath, changedFilePath)))
					.Select(p => p.FullPath)
					.ToList();
				if (importingProjects.Count is 0)
				{
					// Not imported by anything - yet. A newly created Directory.Build.props may now be imported by any project below it.
					importingProjects = outerProjectPaths.Where(p => IsUnder(p, Path.GetDirectoryName(changedFilePath)!)).ToList();
					// The first restore writes <project file>.nuget.g.props/targets into the project's MSBuildProjectExtensionsPath (obj/ by default), which Microsoft.Common.props/targets imports by wildcard.
					// That folder is below the project (or elsewhere, e.g. artifacts/obj), so the check above doesn't find it.
					importingProjects.AddRange(loadedProjects
						.Where(p => p.GlobalProperties.ContainsKey(TargetFrameworkProperty) is false)
						.Where(p => IsRestoreOutputOf(p, changedFilePath))
						.Select(p => p.FullPath));
				}
				affectedProjectPaths.UnionWith(importingProjects);
			}

			// Conditions such as Exists(...) aren't tracked, so a file appearing doesn't make a project dirty - force it.
			// A changed (or deleted, or now invalid) import is re-read from the cache when the project is re-evaluated, so only a changed project file needs the project loading again.
			var projectsToEvaluate = outerProjectPaths.Where(affectedProjectPaths.Contains).ToList();
			return await EvaluateInParallelAsync(projectsToEvaluate, reloadProjectFile: changedPaths.Contains, forceReevaluation: true, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task UnloadAllProjectsAsync()
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			UnloadAllProjects();
		}
		finally
		{
			_lock.Release();
		}
	}

	/// Edits the XML through ProjectRootElement, so the rest of the file is left as it was.
	/// In a ProjectCollection (and so XML cache) of its own, so the shared cache's XML isn't changed in place - our projects pick the change up when the file watcher re-evaluates them.
	public async Task<string> GetOrCreateUserSecretsIdAsync(string projectFilePath)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var project = GetOuterProject(projectFilePath) ?? _projectCollection.LoadProject(projectFilePath);
			var userSecretsId = project.GetPropertyValue("UserSecretsId");
			if (string.IsNullOrWhiteSpace(userSecretsId) is false) return userSecretsId;

			var newUserSecretsId = Guid.NewGuid().ToString();
			using var editCollection = new ProjectCollection();
			var projectRootElement = ProjectRootElement.Open(projectFilePath, editCollection, preserveFormatting: true);
			projectRootElement.AddProperty("UserSecretsId", newUserSecretsId);
			projectRootElement.Save();
			return newUserSecretsId;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// reloadProjectFile: whether a project's own file may have changed, so it needs loading again (from the cache, which re-reads it if it has)
	private async Task<List<ProjectEvaluationDto>> EvaluateInParallelAsync(IReadOnlyCollection<string> projectFilePaths, Func<string, bool> reloadProjectFile, bool forceReevaluation, CancellationToken cancellationToken)
	{
		// Evaluation blocks (MSBuild's ConditionEvaluator serializes evaluating the same condition across projects, so more at once isn't faster) -
		// so it shares design-time builds' throttle, rather than taking thread pool threads a design-time batch running meanwhile needs (its node handshakes, callbacks, logging).
		var results = await Task.WhenAll(projectFilePaths
			.Select(Path.GetFullPath)
			.Distinct(PathComparer)
			.Select(path => Task.Run(() => DesignTimeBuildManager.RunBlockingAsync(() => LoadOrReloadProject(path, reloadProjectFile(path), forceReevaluation), cancellationToken), cancellationToken))).ConfigureAwait(false);
		return [.. results];
	}

	/// One at a time - ProjectCollection.UnloadAllProjects clears the XML cache, which is process-wide, so would clear design-time builds' too
	private void UnloadAllProjects()
	{
		foreach (var project in _projectCollection.LoadedProjects.ToList()) _projectCollection.UnloadProject(project);
	}

	private Project? GetOuterProject(string projectFilePath) => _projectCollection.GetLoadedProjects(projectFilePath)
		.FirstOrDefault(project => project.GlobalProperties.ContainsKey(TargetFrameworkProperty) is false);

	private ProjectEvaluationDto LoadOrReloadProject(string projectFilePath, bool reloadProjectFile, bool forceReevaluation)
	{
		if (File.Exists(projectFilePath) is false)
		{
			foreach (var project in _projectCollection.GetLoadedProjects(projectFilePath).ToList()) _projectCollection.UnloadProject(project);
			return new ProjectEvaluationDto(projectFilePath, new ProjectInstanceEvaluationDto(ProjectLoadStateDto.Missing, null, null, null, null), []);
		}

		try
		{
			var loadedProjects = _projectCollection.GetLoadedProjects(projectFilePath).ToList();
			// Their XML (shared by the outer and inner projects) is as the file was when they were loaded - load them again, rather than Reload the shared cache's XML in place.
			// Unchanged on disk, the cache hands back the same XML, so this costs an evaluation, as Reload did.
			if (reloadProjectFile)
			{
				foreach (var project in loadedProjects) _projectCollection.UnloadProject(project);
				loadedProjects = [];
			}
			var outerProject = loadedProjects.FirstOrDefault(project => project.GlobalProperties.ContainsKey(TargetFrameworkProperty) is false);
			if (outerProject is not null)
			{
				if (forceReevaluation) outerProject.MarkDirty();
				outerProject.ReevaluateIfNecessary();
			}
			else
			{
				outerProject = _projectCollection.LoadProject(projectFilePath);
			}

			var outerResult = ToDto(outerProject);

			var targetFrameworks = outerProject.GetPropertyValue("TargetFrameworks")
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (targetFrameworks.Count is 0)
			{
				foreach (var innerProject in loadedProjects.Where(project => project.GlobalProperties.ContainsKey(TargetFrameworkProperty)))
				{
					_projectCollection.UnloadProject(innerProject);
				}
				return new ProjectEvaluationDto(projectFilePath, outerResult, []);
			}

			var desiredTargetFrameworks = targetFrameworks.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var innerProjectsByTargetFramework = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
			foreach (var innerProject in loadedProjects.Where(project => project.GlobalProperties.ContainsKey(TargetFrameworkProperty)))
			{
				var targetFramework = innerProject.GlobalProperties[TargetFrameworkProperty];
				if (desiredTargetFrameworks.Contains(targetFramework) is false || innerProjectsByTargetFramework.TryAdd(targetFramework, innerProject) is false)
				{
					_projectCollection.UnloadProject(innerProject);
				}
			}

			var tfmSpecificResults = new List<ProjectInstanceEvaluationDto>(targetFrameworks.Count);
			foreach (var targetFramework in targetFrameworks)
			{
				try
				{
					if (innerProjectsByTargetFramework.TryGetValue(targetFramework, out var innerProject))
					{
						if (forceReevaluation) innerProject.MarkDirty();
						innerProject.ReevaluateIfNecessary();
					}
					else
					{
						innerProject = _projectCollection.LoadProject(projectFilePath, new Dictionary<string, string>
						{
							[TargetFrameworkProperty] = targetFramework
						}, toolsVersion: null);
					}
					tfmSpecificResults.Add(ToDto(innerProject));
				}
				catch (InvalidProjectFileException ex)
				{
					tfmSpecificResults.Add(new ProjectInstanceEvaluationDto(ProjectLoadStateDto.Invalid, ToDiagnostic(ex), null, null, null));
				}
			}

			return new ProjectEvaluationDto(projectFilePath, outerResult, tfmSpecificResults);
		}
		catch (InvalidProjectFileException ex)
		{
			return new ProjectEvaluationDto(projectFilePath, new ProjectInstanceEvaluationDto(ProjectLoadStateDto.Invalid, ToDiagnostic(ex), null, null, null), []);
		}
	}

	private static ProjectInstanceEvaluationDto ToDto(Project project)
	{
		var properties = PropertyNames.ToDictionary(name => name, project.GetPropertyValue);
		var packageReferences = project.GetItems("PackageReference")
			.Select(item => new PackageReferenceDto(item.EvaluatedInclude, item.GetMetadataValue("Version")))
			.ToList();
		return new ProjectInstanceEvaluationDto(ProjectLoadStateDto.Loaded, null, properties, project.Xml.Sdk, packageReferences);
	}

	private static ProjectEvaluationDiagnosticDto ToDiagnostic(InvalidProjectFileException ex) =>
		new(ex.ErrorCode, ex.BaseMessage, ex.ErrorSubcategory, ex.HelpLink, ex.ProjectFile, ex.LineNumber, ex.ColumnNumber);

	private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private static bool IsRestoreOutputOf(Project project, string filePath)
	{
		var projectExtensionsPath = project.GetPropertyValue("MSBuildProjectExtensionsPath");
		if (string.IsNullOrWhiteSpace(projectExtensionsPath)) return false;
		var projectExtensionsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectExtensionsPath, project.DirectoryPath));
		return PathComparer.Equals(Path.GetDirectoryName(filePath), projectExtensionsDirectory)
			&& Path.GetFileName(filePath).StartsWith(Path.GetFileName(project.FullPath) + ".", OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static bool IsUnder(string filePath, string directoryPath)
	{
		var relative = Path.GetRelativePath(directoryPath, filePath);
		return relative.StartsWith("..", StringComparison.Ordinal) is false && Path.IsPathRooted(relative) is false;
	}
}
