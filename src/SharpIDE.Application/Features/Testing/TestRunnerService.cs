using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.Testing.Client;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Application.Features.Testing;

public class TestRunnerService(RoslynAnalysis roslynAnalysis, ILogger<TestRunnerService> logger)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;
	private readonly ILogger<TestRunnerService> _logger = logger;

	public async Task<List<TestNode>> DiscoverTestsForSolution(SharpIdeSolutionModel solutionModel)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		List<TestNode> allDiscoveredTestNodes = [];
		foreach (var testProject in testProjects)
		{
			await using var client = await GetInitialisedClient(testProject);
			var testNodes = await DiscoverTestsForProject(client, testProject);
			foreach (var testNode in testNodes) allDiscoveredTestNodes.Add(testNode);
		}
		_logger.LogInformation("Discovered {DiscoveredTestCount} tests", allDiscoveredTestNodes.Count);
		return allDiscoveredTestNodes;
	}

	private async Task<TestNode[]> DiscoverTestsForProject(TestingPlatformClient clientForProject, SharpIdeProjectModel project)
	{
		List<TestNodeUpdate> testNodeUpdates = [];
		var discoveryResponse = await clientForProject.DiscoverTestsAsync(Guid.NewGuid(), node =>
		{
			testNodeUpdates.AddRange(node);
			return Task.CompletedTask;
		});
		await discoveryResponse.WaitCompletionAsync();

		var discoveredTestNodes = testNodeUpdates.Select(x => x.Node).ToArray();
		_logger.LogInformation("Discovered {DiscoveredTestCount} tests for project {ProjectName}", discoveredTestNodes.Length, project.Name.Value);
		return discoveredTestNodes;
	}

	public async Task RunTestsForSolution(SharpIdeSolutionModel solutionModel, Func<TestNodeUpdate[], Task> func)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		foreach (var testProject in testProjects)
		{
			await using var client = await GetInitialisedClient(testProject);
			var discoveredTestNodes = await DiscoverTestsForProject(client, testProject);
			await RunTestsForProject(client, testProject, discoveredTestNodes, func);
		}
	}

	// Assumes it has already been built
	private async Task RunTestsForProject(TestingPlatformClient clientForProject, SharpIdeProjectModel project, TestNode[] testNodes, Func<TestNodeUpdate[], Task> func)
	{
		ResponseListener runRequest = await clientForProject.RunTestsAsync(Guid.NewGuid(), testNodes, func);
		await runRequest.WaitCompletionAsync();
	}

	private async Task<TestingPlatformClient> GetInitialisedClient(SharpIdeProjectModel project)
	{
		var outputDllPath = await _roslynAnalysis.GetOutputDllPathForProject(project);
		var outputExecutablePath = 0 switch
		{
			_ when OperatingSystem.IsWindows() => outputDllPath!.Replace(".dll", ".exe"),
			_ when OperatingSystem.IsLinux() => outputDllPath!.Replace(".dll", ""),
			_ when OperatingSystem.IsMacOS() => outputDllPath!.Replace(".dll", ""),
			_ => throw new PlatformNotSupportedException("Unsupported OS for running tests.")
		};

		var client = await TestingPlatformClientFactory.StartAsServerAndConnectToTheClientAsync(outputExecutablePath);
		await client.InitializeAsync();
		return client;
	}
}
