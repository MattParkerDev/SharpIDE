namespace SharpIDE.MsBuildHost.Contracts;

public enum ProjectLoadStateDto
{
	Loading = 1,
	Loaded,
	Unloaded,
	Invalid,
	Missing
}

/// Converted from an InvalidProjectFileException. Line and column are 1-based, as MSBuild reports them (0 if unknown).
public sealed record ProjectEvaluationDiagnosticDto(string? Code, string Message, string? Subcategory, string? HelpLink, string? File, int Line, int Column);

public sealed record PackageReferenceDto(string Name, string? Version);

/// The result of evaluating one project instance - the outer project, or one per TFM when TargetFrameworks is set.
/// Properties, Sdk and PackageReferences are only set when LoadState is Loaded; Diagnostic only when it is Invalid.
public sealed record ProjectInstanceEvaluationDto(
	ProjectLoadStateDto LoadState,
	ProjectEvaluationDiagnosticDto? Diagnostic,
	Dictionary<string, string>? Properties,
	string? Sdk,
	List<PackageReferenceDto>? PackageReferences);

/// TfmSpecific is empty for single TFM projects
public sealed record ProjectEvaluationDto(string ProjectFilePath, ProjectInstanceEvaluationDto Outer, List<ProjectInstanceEvaluationDto> TfmSpecific);

/// Properties are read from the project's state after Build;ComputeRunArguments - RunCommand, RunArguments, RunWorkingDirectory, TargetPath, OutDir
public sealed record RunBuildResultDto(BuildResultDto BuildResult, string? ErrorMessage, Dictionary<string, string> Properties);
