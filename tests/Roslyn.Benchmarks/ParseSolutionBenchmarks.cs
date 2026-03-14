using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Roslyn.Benchmarks;

public class ParseSolutionBenchmarks
{
	private static readonly string s_solutionFilePath = Path.Combine(
		FindRepositoryRoot(),
		"src",
		"SharpIDE.Godot",
		"SharpIDE.Godot.sln");
	private MSBuildWorkspace _workspace = null!;

	[IterationSetup]
	public void IterationSetup()
	{
		_workspace = MSBuildWorkspace.Create();
	}

	// | ParseSolutionFileFromPath | 1.488 s | 0.0063 s | 0.0059 s |
	[Benchmark]
	public async Task<Solution> ParseSolutionFileFromPath()
	{
		var solution = await _workspace.OpenSolutionAsync(s_solutionFilePath);
		return solution;
	}

	[IterationCleanup]
	public void IterationCleanup()
	{
		_workspace?.CloseSolution();
	}

	private static string FindRepositoryRoot()
	{
		var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
		while (currentDirectory is not null)
		{
			if (currentDirectory.EnumerateFileSystemInfos(".git").Any())
			{
				return currentDirectory.FullName;
			}

			currentDirectory = currentDirectory.Parent;
		}

		throw new InvalidOperationException($"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
	}
}
