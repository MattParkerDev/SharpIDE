using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis.MSBuild;

namespace Roslyn.Benchmarks;

public class CreateWorkspaceBenchmarks
{
	// | Method                    | Mean     | Error    | StdDev   |
	// |-------------------------- |---------:|---------:|---------:|
	// | CreateWorkspaceNoParams   | 10.88 us | 0.045 us | 0.042 us |
	// | CreateWorkspaceWithParams | 11.22 us | 0.072 us | 0.060 us |
	[Benchmark]
	public MSBuildWorkspace CreateWorkspaceNoParams()
	{
		var workspace = MSBuildWorkspace.Create();
		return workspace;
	}

	[Benchmark]
	public MSBuildWorkspace CreateWorkspaceWithParams()
	{
		var properties = new Dictionary<string, string>
		{
			{ "Configuration", "Debug" },
			{ "Platform", "AnyCPU" }
		};
		var workspace = MSBuildWorkspace.Create(properties);
		return workspace;
	}

}
