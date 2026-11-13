using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost.DesignTime;

// Adapted from Roslyn's BuildHost ProjectBuildManager and ProjectFile
// https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/BuildHost/Build/ProjectBuildManager.cs

/// Design-time builds projects, for the IDE's Roslyn workspace. Only runs in a host started with --design-time, so it owns BuildManager.DefaultBuildManager.
/// Long-lived: the ProjectCollection (and its XML cache, which re-reads changed files) lives as long as the host,
/// while each batch gets its own build session - BeginBuild resets the session's configuration and results caches, so no build results outlive a batch.
public sealed class DesignTimeBuildManager
{
	private static readonly Dictionary<string, string> DefaultGlobalProperties = new()
	{
		// Don't build project references
		["DesignTimeBuild"] = bool.TrueString,
		// Forces CoreCompile to run (returning the compiler command line) even if all its inputs and outputs are up to date
		["NonExistentFile"] = "__NonExistentSubDir__\\__NonExistentFile__",
		["BuildProjectReferences"] = bool.FalseString,
		["BuildingProject"] = bool.FalseString,
		// Return the compiler's command line args, without running the compiler
		["ProvideCommandLineArgs"] = bool.TrueString,
		["SkipCompilerExecution"] = bool.TrueString,
		["ContinueOnError"] = "ErrorAndContinue",
		// Referenced projects use this project's Configuration and Platform, for a more likely to be correct output path
		["ShouldUnsetParentConfigurationAndPlatform"] = bool.FalseString,
	};

	// Roughly matches VS' project load settings for design-time builds
	private const ProjectLoadSettings LoadSettings = ProjectLoadSettings.RejectCircularImports
		| ProjectLoadSettings.IgnoreEmptyImports
		| ProjectLoadSettings.IgnoreMissingImports
		| ProjectLoadSettings.IgnoreInvalidImports
		| ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition
		| ProjectLoadSettings.FailOnUnresolvedSdk;

	private static readonly string[] RequiredTargets = ["Compile", "CoreCompile"];
	private static readonly string[] OptionalTargets = ["DesignTimeMarkupCompilation"];

	// One batch (build session) at a time. Projects within a batch are built in parallel.
	private readonly SemaphoreSlim _batchLock = new(1, 1);
	private ProjectCollection? _projectCollection;
	private Dictionary<string, string>? _projectCollectionGlobalProperties;

	public async Task<List<DesignTimeBuildResultDto>> DesignTimeBuildAsync(List<string> projectFilePaths, Dictionary<string, string> globalProperties, CancellationToken cancellationToken)
	{
		await _batchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var projectCollection = GetProjectCollection(globalProperties);
			var logger = new DesignTimeDiagnosticLogger();
			var buildManager = BuildManager.DefaultBuildManager;
			buildManager.BeginBuild(new BuildParameters(projectCollection)
			{
				Loggers = [logger],
				MaxNodeCount = MaxNodeCount,
				// Out of proc nodes outlive the batch (and this host), so later batches don't wait for new ones to start. Idle ones are shared with other MSBuild processes on the same SDK (our build host, the CLI).
				// Left over ones exit after MSBuild's idle timeout (15 minutes), as the CLI's do.
				EnableNodeReuse = true,
			});
			try
			{
				// MSBuild can't cancel a single submission - the batch is this session's only work, so cancel it all
				await using var _ = cancellationToken.Register(buildManager.CancelAllSubmissions).ConfigureAwait(false);
				var results = await Task.WhenAll(projectFilePaths
					.Select(Path.GetFullPath)
					.Distinct(PathComparer)
					.Select(path => Task.Run(() => DesignTimeBuildProjectAsync(projectCollection, buildManager, logger, path, cancellationToken), cancellationToken))).ConfigureAwait(false);
				return [.. results];
			}
			finally
			{
				buildManager.EndBuild();
			}
		}
		finally
		{
			_batchLock.Release();
		}
	}

	public async Task<string?> TryGetProjectOutputPathAsync(string projectFilePath, CancellationToken cancellationToken)
	{
		await _batchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var projectCollection = GetProjectCollection(_projectCollectionGlobalProperties ?? []);
			var project = new Project(projectFilePath, null, null, projectCollection, LoadSettings);
			try
			{
				return project.GetPropertyValue("TargetPath");
			}
			finally
			{
				projectCollection.UnloadProject(project);
			}
		}
		catch (Exception ex) when (ex is InvalidProjectFileException or IOException)
		{
			return null;
		}
		finally
		{
			_batchLock.Release();
		}
	}

	// 4 nodes (reused between batches) by default. Measured, warm batches: 12 projects ~350ms vs ~480ms with 1 node, 131 projects ~4-5s vs ~7-9s.
	// 8 were no faster than 4. A batch that has to start nodes (the first, after they've idled out) is slower for a small solution - sharpdbg ~1.0s vs ~0.9s. SHARPIDE_DESIGNTIME_MAXNODECOUNT overrides it.
	private static int MaxNodeCount => int.TryParse(Environment.GetEnvironmentVariable("SHARPIDE_DESIGNTIME_MAXNODECOUNT"), out var count) && count > 0 ? count : Math.Min(4, Environment.ProcessorCount);

	/// The collection's global properties are fixed, so a different set (e.g. another SolutionDir) gets a new collection
	private ProjectCollection GetProjectCollection(Dictionary<string, string> globalProperties)
	{
		if (_projectCollection is not null && _projectCollectionGlobalProperties is not null
			&& _projectCollectionGlobalProperties.Count == globalProperties.Count && _projectCollectionGlobalProperties.All(p => globalProperties.TryGetValue(p.Key, out var value) && value == p.Value))
		{
			return _projectCollection;
		}

		// One at a time - UnloadAllProjects clears the XML cache, which is process-wide, so would clear the ProjectEvaluator's too
		if (_projectCollection is not null) foreach (var project in _projectCollection.LoadedProjects.ToList()) _projectCollection.UnloadProject(project);
		_projectCollection?.Dispose();
		var allProperties = new Dictionary<string, string>(DefaultGlobalProperties);
		foreach (var (key, value) in globalProperties) allProperties[key] = value;
		// reuseProjectRootElementCache: XML read from disk is kept, and re-read when a file changes (checked by timestamp), as dotnet-watch does.
		// The cache is process-wide - shared with the ProjectEvaluator's collection, and with this one's replacements
		_projectCollection = new ProjectCollection(
			globalProperties: allProperties,
			loggers: [],
			remoteLoggers: [],
			ToolsetDefinitionLocations.Default,
			maxNodeCount: 1,
			onlyLogCriticalEvents: false,
			loadProjectsReadOnly: false,
			useAsynchronousLogging: false,
			reuseProjectRootElementCache: true);
		_projectCollectionGlobalProperties = new Dictionary<string, string>(globalProperties);
		return _projectCollection;
	}

	private static async Task<DesignTimeBuildResultDto> DesignTimeBuildProjectAsync(ProjectCollection projectCollection, BuildManager buildManager, DesignTimeDiagnosticLogger logger, string projectFilePath, CancellationToken cancellationToken)
	{
		var language = GetLanguage(projectFilePath);
		var log = new DiagnosticLog();
		Project project;
		try
		{
			project = await RunBlockingAsync(() => new Project(projectFilePath, null, null, projectCollection, LoadSettings), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Can't build a project that failed to load - an empty project, as Roslyn's BuildHost does
			log.Add(ex, projectFilePath);
			return new DesignTimeBuildResultDto(projectFilePath, [ProjectFileInfoDto.CreateEmpty(language, projectFilePath)], log.ToArray());
		}

		try
		{
			var projectInstances = await BuildProjectInstancesAsync(project, buildManager, logger, log, cancellationToken).ConfigureAwait(false);
			var projectFileInfos = await RunBlockingAsync(() => projectInstances.Select(instance => new ProjectInstanceReader(language, instance.ProjectInstance, instance.Project).CreateProjectFileInfo()).ToArray(), cancellationToken).ConfigureAwait(false);
			return new DesignTimeBuildResultDto(projectFilePath, projectFileInfos, log.ToArray());
		}
		finally
		{
			foreach (var loadedProject in projectCollection.GetLoadedProjects(projectFilePath).ToList()) projectCollection.UnloadProject(loadedProject);
		}
	}

	/// One per target framework - a project with TargetFrameworks but no TargetFramework is built once per TargetFramework
	private static async Task<List<(ProjectInstance ProjectInstance, Project Project)>> BuildProjectInstancesAsync(Project project, BuildManager buildManager, DesignTimeDiagnosticLogger logger, DiagnosticLog log, CancellationToken cancellationToken)
	{
		var targetFramework = project.GetPropertyValue("TargetFramework");
		var targetFrameworks = project.GetPropertyValue("TargetFrameworks");
		if (string.IsNullOrEmpty(targetFramework) is false || string.IsNullOrEmpty(targetFrameworks))
		{
			return [(await BuildProjectInstanceAsync(project, buildManager, logger, log, cancellationToken).ConfigureAwait(false), project)];
		}

		// A separate evaluation per TFM, so they can build in parallel
		var innerProjects = await Task.WhenAll(targetFrameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(tfm => RunBlockingAsync(() => new Project(project.FullPath, new Dictionary<string, string>(project.GlobalProperties) { ["TargetFramework"] = tfm }, null, project.ProjectCollection, LoadSettings), cancellationToken))).ConfigureAwait(false);
		var instances = await Task.WhenAll(innerProjects.Select(innerProject => BuildProjectInstanceAsync(innerProject, buildManager, logger, log, cancellationToken))).ConfigureAwait(false);
		return [.. instances.Zip(innerProjects)];
	}

	private static async Task<ProjectInstance> BuildProjectInstanceAsync(Project project, BuildManager buildManager, DesignTimeDiagnosticLogger logger, DiagnosticLog log, CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var (projectInstance, submission) = await RunBlockingAsync<(ProjectInstance, BuildSubmission?)>(() =>
		{
			// The executed instance holds the project's final state after the design-time build
			var projectInstance = project.CreateProjectInstance();
			var missingTarget = RequiredTargets.FirstOrDefault(target => projectInstance.Targets.ContainsKey(target) is false);
			if (missingTarget is not null)
			{
				log.Add(DiagnosticLogItemKindDto.Error, $"Project does not contain '{missingTarget}' target.", project.FullPath);
				return (projectInstance, null);
			}
			string[] targets = [.. RequiredTargets, .. OptionalTargets.Where(projectInstance.Targets.ContainsKey)];

			cancellationToken.ThrowIfCancellationRequested();
			// The build may happen on another node, so ask for its state back - just what ProjectInstanceReader reads, rather than the whole state serialized back over the node's pipe
			var submission = buildManager.PendBuildRequest(new BuildRequestData(projectInstance, targets, hostServices: null, BuildRequestDataFlags.ProvideSubsetOfStateAfterBuild,
				propertiesToTransfer: null, ProjectInstanceReader.RequestedProjectState));
			logger.RegisterLog(submission.SubmissionId, log);
			submission.ExecuteAsync(sub => tcs.TrySetResult(sub.BuildResult!), null);
			return (projectInstance, submission);
		}, cancellationToken).ConfigureAwait(false);
		if (submission is null) return projectInstance;

		try
		{
			var result = await tcs.Task.ConfigureAwait(false);
			if (result.OverallResult is BuildResultCode.Failure && result.Exception is not null) log.Add(result.Exception, projectInstance.FullPath);
			return result.ProjectStateAfterBuild ?? projectInstance;
		}
		finally
		{
			logger.UnregisterLog(submission.SubmissionId);
		}
	}

	// Also used by the ProjectEvaluator, which evaluates in this host alongside design-time builds - one cap on blocked thread pool threads for the whole process.
	// Evaluation, PendBuildRequest (which waits for the BuildManager's lock) and reading results all block - so only a few at once, rather than one thread pool thread per project.
	// Otherwise the pool is starved, and with out of proc nodes so is MSBuild's own async work (e.g. node handshakes) - 131 projects with 4 nodes took ~8-16s per batch,
	// sometimes stalling for 30s while a node handshake timed out, vs a steady ~4.5s with enough threads.
	// Most of the blocking is MSBuild's ConditionEvaluator serializing evaluations of the same condition - this can be removed once https://github.com/dotnet/msbuild/pull/15544 is merged.
	private static readonly SemaphoreSlim BlockingWorkThrottle = new(Math.Max(2, Environment.ProcessorCount / 2));

	internal static async Task<T> RunBlockingAsync<T>(Func<T> work, CancellationToken cancellationToken)
	{
		await BlockingWorkThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return work();
		}
		finally
		{
			BlockingWorkThrottle.Release();
		}
	}

	private static string GetLanguage(string projectFilePath) => Path.GetExtension(projectFilePath).ToLowerInvariant() switch
	{
		".vbproj" => LanguageNames.VisualBasic,
		".fsproj" => LanguageNames.FSharp,
		_ => LanguageNames.CSharp
	};

	private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
