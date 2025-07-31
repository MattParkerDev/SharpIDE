using System.Diagnostics;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace SharpIDE.Application.Features.Build;

public class BuildService
{
	public async Task BuildSolutionAsync(string solutionFilePath)
	{
		var buildParameters = new BuildParameters
		{
			Loggers =
			[
				//new BinaryLogger { Parameters = "msbuild.binlog" },
				new ConsoleLogger(LoggerVerbosity.Quiet),
			],
		};
		var buildRequest = new BuildRequestData(
			projectFullPath : solutionFilePath,
			globalProperties: new Dictionary<string, string?>(),
			toolsVersion: null,
			targetsToBuild: ["Restore", "Build"],
			hostServices: null,
			flags: BuildRequestDataFlags.None);

		await Task.Run(() =>
		{
			var timer = Stopwatch.StartNew();
			var buildResult = BuildManager.DefaultBuildManager.Build(buildParameters, buildRequest);
			timer.Stop();
			Console.WriteLine($"Build result: {buildResult.OverallResult} in {timer.ElapsedMilliseconds}ms");
		}).ConfigureAwait(false);
	}
}

// To build a single project
// var solutionFile = GetNodesInSolution.ParseSolutionFileFromPath(_solutionFilePath);
// ArgumentNullException.ThrowIfNull(solutionFile);
// var projects = GetNodesInSolution.GetCSharpProjectObjectsFromSolutionFile(solutionFile);
// var projectRoot = projects.First();
// var buildRequest = new BuildRequestData(
// 	ProjectInstance.FromProjectRootElement(projectRoot, new ProjectOptions()),
// 	targetsToBuild: ["Restore", "Build"]);
