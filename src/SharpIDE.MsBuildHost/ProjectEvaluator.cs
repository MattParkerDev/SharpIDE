using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost;

/// Evaluation only - never runs targets, and never touches BuildManager, so it can run alongside real builds in this process.
/// (If a target ever needs running for evaluation, don't use ProjectInstance.Build - it opens a BuildManager.DefaultBuildManager session, which throws if a build already has one open.
/// Send it through the same path real builds use instead.)
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

	// Fine to share with builds - new BuildParameters() gets its own ProjectRootElementCache
	private readonly ProjectCollection _projectCollection = ProjectCollection.GlobalProjectCollection;
	// One batch at a time. Projects within a batch are evaluated in parallel.
	private readonly SemaphoreSlim _lock = new(1, 1);

	public async Task<List<ProjectEvaluationDto>> EvaluateProjectsAsync(List<string> projectFilePaths, CancellationToken cancellationToken)
	{
		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return EvaluateInParallel(projectFilePaths, reloadProjectFile: true, forceReevaluation: false, cancellationToken);
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

			var unloadEverything = false;
			var affectedProjectPaths = new HashSet<string>(PathComparer);
			foreach (var changedFilePath in changedFilePaths.Select(Path.GetFullPath).Distinct(PathComparer))
			{
				var projectRootElement = ProjectRootElement.TryOpen(changedFilePath, _projectCollection);
				if (projectRootElement is not null)
				{
					try
					{
						projectRootElement.Reload(throwIfUnsavedChanges: false);
					}
					catch (Exception ex) when (ex is InvalidProjectFileException or IOException or UnauthorizedAccessException)
					{
						// e.g. deleted, or now invalid XML - start again from disk
						unloadEverything = true;
						break;
					}
				}

				var importingProjects = loadedProjects
					.Where(p => PathComparer.Equals(p.FullPath, changedFilePath) || p.Imports.Any(i => PathComparer.Equals(i.ImportedProject.FullPath, changedFilePath)))
					.Select(p => p.FullPath)
					.ToList();
				if (importingProjects.Count is 0)
				{
					// Not imported by anything - yet. A newly created Directory.Build.props, or a nuget.g.props written by the first restore, may now be imported by any project below it.
					importingProjects = outerProjectPaths.Where(p => IsUnder(p, Path.GetDirectoryName(changedFilePath)!)).ToList();
				}
				affectedProjectPaths.UnionWith(importingProjects);
			}

			if (unloadEverything)
			{
				_projectCollection.UnloadAllProjects();
				return EvaluateInParallel(outerProjectPaths, reloadProjectFile: false, forceReevaluation: false, cancellationToken);
			}

			// Conditions such as Exists(...) aren't tracked, so a file appearing doesn't make a project dirty - force it
			var projectsToEvaluate = outerProjectPaths.Where(affectedProjectPaths.Contains).ToList();
			return EvaluateInParallel(projectsToEvaluate, reloadProjectFile: false, forceReevaluation: true, cancellationToken);
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
			_projectCollection.UnloadAllProjects();
		}
		finally
		{
			_lock.Release();
		}
	}

	/// Edits the XML through ProjectRootElement, so the rest of the file is left as it was
	public async Task<string> GetOrCreateUserSecretsIdAsync(string projectFilePath)
	{
		await _lock.WaitAsync().ConfigureAwait(false);
		try
		{
			var project = GetOuterProject(projectFilePath) ?? _projectCollection.LoadProject(projectFilePath);
			var userSecretsId = project.GetPropertyValue("UserSecretsId");
			if (string.IsNullOrWhiteSpace(userSecretsId) is false) return userSecretsId;

			var newUserSecretsId = Guid.NewGuid().ToString();
			project.Xml.AddProperty("UserSecretsId", newUserSecretsId);
			project.Save();
			return newUserSecretsId;
		}
		finally
		{
			_lock.Release();
		}
	}

	private List<ProjectEvaluationDto> EvaluateInParallel(IReadOnlyCollection<string> projectFilePaths, bool reloadProjectFile, bool forceReevaluation, CancellationToken cancellationToken)
	{
		var distinctPaths = projectFilePaths.Select(Path.GetFullPath).Distinct(PathComparer).ToList();
		var results = new ProjectEvaluationDto[distinctPaths.Count];
		Parallel.For(0, distinctPaths.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
		{
			results[i] = LoadOrReloadProject(distinctPaths[i], reloadProjectFile, forceReevaluation);
		});
		return results.ToList();
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
			var outerProject = loadedProjects.FirstOrDefault(project => project.GlobalProperties.ContainsKey(TargetFrameworkProperty) is false);
			if (outerProject is not null)
			{
				// Shared by the outer and inner projects
				if (reloadProjectFile) outerProject.Xml.Reload(throwIfUnsavedChanges: false);
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

	private static bool IsUnder(string filePath, string directoryPath)
	{
		var relative = Path.GetRelativePath(directoryPath, filePath);
		return relative.StartsWith("..", StringComparison.Ordinal) is false && Path.IsPathRooted(relative) is false;
	}
}
