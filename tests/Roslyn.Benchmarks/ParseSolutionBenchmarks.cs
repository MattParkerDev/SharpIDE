using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Roslyn.Benchmarks;

public class ParseSolutionBenchmarks
{
	private const string _solutionFilePath = "C:/Users/Matthew/Documents/Git/StatusApp/StatusApp.sln";
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
		var solution = await _workspace.OpenSolutionAsync(_solutionFilePath);
		return solution;
	}

	[IterationCleanup]
	public void IterationCleanup()
	{
		_workspace?.CloseSolution();
	}
}
