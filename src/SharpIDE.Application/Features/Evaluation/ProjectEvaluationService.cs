using Ardalis.GuardClauses;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Threading;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.Application.Features.Evaluation;

/// MSBuild evaluation of the solution's projects, served by SharpIDE.MsBuildHost - the IDE process never loads MSBuild.
public class ProjectEvaluationService
{
	private readonly BuildService _buildService;
	private readonly ILogger<ProjectEvaluationService> _logger;
	private readonly AsyncBatchingWorkQueue<string> _importFileChangedQueue;

	public SharpIdeSolutionModel? SolutionModel { get; set; }

	public ProjectEvaluationService(BuildService buildService, ILogger<ProjectEvaluationService> logger)
	{
		_buildService = buildService;
		_logger = logger;
		// A restore writes several obj files in quick succession
		_importFileChangedQueue = new AsyncBatchingWorkQueue<string>(TimeSpan.FromMilliseconds(300), ProcessImportFileChangesAsync, PathComparison.Comparer, IAsynchronousOperationListener.Instance, CancellationToken.None);
		_buildService.HostRestarted.Subscribe(OnHostRestarted);
	}

	/// Files projects import - Directory.Build.props/targets, Directory.Packages.props, the obj/*.nuget.g.props/targets restore writes, and any other props/targets
	public static bool IsMsBuildImportFile(string filePath) =>
		filePath.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

	/// Evaluates (re-reading their project files) the projects, updating their load results
	public async Task EvaluateProjectsAsync(IReadOnlyCollection<SharpIdeProjectModel> projects, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluationService)}.{nameof(EvaluateProjectsAsync)}");
		if (projects.Count is 0) return;
		foreach (var project in projects) project.BeginEvaluation();
		try
		{
			var rpc = await _buildService.GetRpcAsync(cancellationToken).ConfigureAwait(false);
			var results = await rpc.EvaluateProjectsAsync(projects.Select(p => p.FilePath).ToList(), cancellationToken).ConfigureAwait(false);
			var resultsByPath = results.ToDictionary(r => r.ProjectFilePath, PathComparison.Comparer);
			foreach (var project in projects)
			{
				project.ApplyEvaluationResult(resultsByPath.TryGetValue(project.FilePath, out var result)
					? ToLoadResult(result)
					: FailedLoadResult(project.FilePath, "SharpIDE.MsBuildHost returned no evaluation for this project"));
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to evaluate {ProjectCount} projects in SharpIDE.MsBuildHost", projects.Count);
			foreach (var project in projects) project.ApplyEvaluationResult(FailedLoadResult(project.FilePath, $"Evaluation failed: {ex.Message}"));
		}
	}

	/// Imported files changed - reloads them in the host, and updates whichever projects they affect
	public async Task ReloadForChangedFilesAsync(IReadOnlyCollection<string> changedFilePaths, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(ProjectEvaluationService)}.{nameof(ReloadForChangedFilesAsync)}");
		var solutionModel = SolutionModel;
		if (solutionModel is null || changedFilePaths.Count is 0) return;
		try
		{
			var rpc = await _buildService.GetRpcAsync(cancellationToken).ConfigureAwait(false);
			var results = await rpc.ReloadProjectsAsync(changedFilePaths.ToList(), cancellationToken).ConfigureAwait(false);
			foreach (var result in results)
			{
				var project = solutionModel.AllProjects.FirstOrDefault(p => PathComparison.Comparer.Equals(p.FilePath, result.ProjectFilePath));
				project?.ApplyEvaluationResult(ToLoadResult(result));
			}
			_logger.LogInformation("Re-evaluated {ProjectCount} projects after {FileCount} imported files changed", results.Count, changedFilePaths.Count);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to re-evaluate projects after imported files changed");
		}
	}

	/// e.g. from the file watcher, for files under obj, which are otherwise ignored
	public void ImportFileChanged(string filePath) => _importFileChangedQueue.AddWork(filePath);

	public async Task<Guid> GetOrCreateUserSecretsIdAsync(SharpIdeProjectModel projectModel, CancellationToken cancellationToken = default)
	{
		Guard.Against.Null(projectModel);
		var rpc = await _buildService.GetRpcAsync(cancellationToken).ConfigureAwait(false);
		var userSecretsId = await rpc.GetOrCreateUserSecretsIdAsync(projectModel.FilePath).ConfigureAwait(false);
		return Guid.Parse(userSecretsId);
	}

	private async ValueTask ProcessImportFileChangesAsync(Microsoft.CodeAnalysis.Collections.ImmutableSegmentedList<string> filePaths, CancellationToken cancellationToken)
	{
		await ReloadForChangedFilesAsync(filePaths.ToList(), cancellationToken).ConfigureAwait(false);
	}

	private async Task OnHostRestarted()
	{
		var solutionModel = SolutionModel;
		if (solutionModel is null) return;
		_logger.LogInformation("SharpIDE.MsBuildHost restarted - re-evaluating all projects");
		await EvaluateProjectsAsync(solutionModel.AllProjects.ToList()).ConfigureAwait(false);
	}

	private static MsBuildProjectLoadResult ToLoadResult(ProjectEvaluationDto dto)
	{
		var outer = ToInstanceLoadResult(dto.Outer, dto.ProjectFilePath);
		var tfmSpecific = dto.TfmSpecific.Select(s => ToInstanceLoadResult(s, dto.ProjectFilePath)).ToList();
		return new MsBuildProjectLoadResult
		{
			OuterProjectLoadResult = outer,
			TfmSpecificLoadResults = tfmSpecific,
			DefaultActiveProjectLoadResult = tfmSpecific.Count is 0 ? outer : ProjectEvaluation.GetActiveProjectLoadResult(tfmSpecific)
		};
	}

	private static MsBuildProjectInstanceLoadResult ToInstanceLoadResult(ProjectInstanceEvaluationDto dto, string projectFilePath)
	{
		var loadState = dto.LoadState switch
		{
			ProjectLoadStateDto.Loading => MsBuildProjectLoadState.Loading,
			ProjectLoadStateDto.Loaded => MsBuildProjectLoadState.Loaded,
			ProjectLoadStateDto.Unloaded => MsBuildProjectLoadState.Unloaded,
			ProjectLoadStateDto.Invalid => MsBuildProjectLoadState.Invalid,
			ProjectLoadStateDto.Missing => MsBuildProjectLoadState.Missing,
			_ => throw new ArgumentOutOfRangeException(nameof(dto), dto.LoadState, null)
		};
		var snapshot = loadState is MsBuildProjectLoadState.Loaded
			? new MsBuildProjectSnapshot(
				dto.Properties ?? [],
				dto.Sdk ?? string.Empty,
				(dto.PackageReferences ?? []).Select(p => new MsBuildPackageReference(p.Name, p.Version)).ToList())
			: null;
		var diagnostic = dto.Diagnostic is null ? null : ToDiagnostic(dto.Diagnostic, projectFilePath);
		if (loadState is MsBuildProjectLoadState.Invalid && diagnostic is null) diagnostic = CreateDiagnostic("MSB0000", "The project could not be evaluated", projectFilePath, 1, 1);
		return new MsBuildProjectInstanceLoadResult { LoadState = loadState, Snapshot = snapshot, Diagnostic = diagnostic };
	}

	private static MsBuildProjectLoadResult FailedLoadResult(string projectFilePath, string message)
	{
		var invalid = new MsBuildProjectInstanceLoadResult
		{
			LoadState = MsBuildProjectLoadState.Invalid,
			Diagnostic = CreateDiagnostic("SHARPIDE0001", message, projectFilePath, 1, 1)
		};
		return new MsBuildProjectLoadResult { OuterProjectLoadResult = invalid, TfmSpecificLoadResults = [], DefaultActiveProjectLoadResult = invalid };
	}

	private static SharpIdeDiagnostic ToDiagnostic(ProjectEvaluationDiagnosticDto dto, string projectFilePath) =>
		CreateDiagnostic(dto.Code ?? "MSBuild", dto.Message, string.IsNullOrEmpty(dto.File) ? projectFilePath : dto.File, dto.Line, dto.Column, dto.Subcategory, dto.HelpLink);

	private static SharpIdeDiagnostic CreateDiagnostic(string code, string message, string filePath, int line, int column, string? subcategory = null, string? helpLink = null)
	{
		// MSBuild lines and columns are 1-based, and 0 when unknown
		var linePosition = new LinePosition(Math.Max(line - 1, 0), Math.Max(column - 1, 0));
		var linePositionSpan = new LinePositionSpan(linePosition, linePosition);
		var diagnostic = Diagnostic.Create(new DiagnosticDescriptor(id: code, title: string.Empty, message, subcategory ?? "MSBuild", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: helpLink), Location.Create(filePath, TextSpan.FromBounds(0, 0), linePositionSpan));
		return new SharpIdeDiagnostic(linePositionSpan, diagnostic, filePath);
	}
}
