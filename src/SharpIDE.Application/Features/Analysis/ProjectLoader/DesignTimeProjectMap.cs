using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using SharpIDE.Application.Features.FileSystem;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.Application.Features.Analysis.ProjectLoader;

// Adapted from Roslyn's ProjectMap, keyed on our ProjectFileInfoDto (Roslyn's takes its own ProjectFileInfo)
// https://github.com/dotnet/roslyn/blob/main/src/Workspaces/MSBuild/Core/MSBuild/ProjectMap.cs

/// Project paths and output paths to ProjectIds, and the ProjectInfos created so far. A project path can have several ProjectIds - one per target framework.
public sealed class DesignTimeProjectMap
{
	private readonly Dictionary<string, HashSet<ProjectId>> _projectPathToProjectIdsMap = new(PathComparison.Comparer);
	private readonly Dictionary<string, ImmutableArray<ProjectInfo>> _projectPathToProjectInfosMap = new(PathComparison.Comparer);
	private readonly Dictionary<ProjectId, string> _projectIdToOutputFilePathMap = [];
	private readonly Dictionary<ProjectId, string> _projectIdToOutputRefFilePathMap = [];

	internal void AddProjectInfo(ProjectInfo projectInfo)
	{
		var projectFilePath = projectInfo.FilePath;
		if (string.IsNullOrEmpty(projectFilePath)) throw new ArgumentException("Project does not have a path.");

		var projectInfos = _projectPathToProjectInfosMap.GetValueOrDefault(projectFilePath, []);
		if (projectInfos.Any(pi => pi.Id == projectInfo.Id)) throw new ArgumentException("Project already added.");
		_projectPathToProjectInfosMap[projectFilePath] = projectInfos.Add(projectInfo);
	}

	internal ProjectId GetOrCreateProjectId(string projectPath)
	{
		if (_projectPathToProjectIdsMap.TryGetValue(projectPath, out var projectIds) is false)
		{
			projectIds = [];
			_projectPathToProjectIdsMap.Add(projectPath, projectIds);
		}

		return projectIds.Count == 1
			? projectIds.Single()
			: CreateProjectId(projectPath, outputFilePath: null, outputRefFilePath: null);
	}

	internal ProjectId GetOrCreateProjectId(ProjectFileInfoDto projectFileInfo)
	{
		var projectPath = projectFileInfo.FilePath;
		var outputFilePath = projectFileInfo.OutputFilePath;
		var outputRefFilePath = projectFileInfo.OutputRefFilePath;

		if (projectPath is not null && TryGetIdsByProjectPath(projectPath, out var projectIds))
		{
			if (TryFindPathInProjectIdSet(outputRefFilePath, _projectIdToOutputRefFilePathMap, projectIds, out var projectId) ||
				TryFindPathInProjectIdSet(outputFilePath, _projectIdToOutputFilePathMap, projectIds, out projectId))
			{
				return projectId;
			}
		}

		return CreateProjectId(projectPath, outputFilePath, outputRefFilePath);
	}

	internal bool TryGetIdsByProjectPath(string projectPath, [NotNullWhen(true)] out HashSet<ProjectId>? ids) => _projectPathToProjectIdsMap.TryGetValue(projectPath, out ids);
	internal bool TryGetOutputFilePathById(ProjectId id, [NotNullWhen(true)] out string? outputFilePath) => _projectIdToOutputFilePathMap.TryGetValue(id, out outputFilePath);
	internal bool TryGetOutputRefFilePathById(ProjectId id, [NotNullWhen(true)] out string? outputRefFilePath) => _projectIdToOutputRefFilePathMap.TryGetValue(id, out outputRefFilePath);
	internal string? GetOutputFilePathById(ProjectId projectId) => _projectIdToOutputFilePathMap.GetValueOrDefault(projectId);
	internal string? GetOutputRefFilePathById(ProjectId projectId) => _projectIdToOutputRefFilePathMap.GetValueOrDefault(projectId);
	internal bool TryGetProjectInfosByProjectPath(string projectPath, out ImmutableArray<ProjectInfo> projectInfos) => _projectPathToProjectInfosMap.TryGetValue(projectPath, out projectInfos);

	private ProjectId CreateProjectId(string? projectPath, string? outputFilePath, string? outputRefFilePath)
	{
		var projectId = ProjectId.CreateNewId(debugName: projectPath);
		if (string.IsNullOrEmpty(projectPath) is false)
		{
			if (_projectPathToProjectIdsMap.TryGetValue(projectPath, out var projectIds) is false) _projectPathToProjectIdsMap[projectPath] = projectIds = [];
			projectIds.Add(projectId);
		}
		if (string.IsNullOrEmpty(outputFilePath) is false) _projectIdToOutputFilePathMap.Add(projectId, outputFilePath);
		if (string.IsNullOrEmpty(outputRefFilePath) is false) _projectIdToOutputRefFilePathMap.Add(projectId, outputRefFilePath);
		return projectId;
	}

	private static bool TryFindPathInProjectIdSet(string? path, Dictionary<ProjectId, string> pathsById, HashSet<ProjectId> set, [NotNullWhen(true)] out ProjectId? result)
	{
		if (string.IsNullOrEmpty(path) is false)
		{
			foreach (var id in set)
			{
				if (pathsById.TryGetValue(id, out var p) && PathComparison.Comparer.Equals(p, path))
				{
					result = id;
					return true;
				}
			}
		}

		result = null;
		return false;
	}
}
