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

	public async Task<List<TestNode>> DiscoverTests(SharpIdeSolutionModel solutionModel)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		List<TestNode> allDiscoveredTestNodes = [];
		foreach (var testProject in testProjects)
		{
			var testNodes = await DiscoverTestsForProject(testProject);
			foreach (var testNode in testNodes) allDiscoveredTestNodes.Add(testNode);
		}
		_logger.LogInformation("Discovered {DiscoveredTestCount} tests", allDiscoveredTestNodes.Count);
		return allDiscoveredTestNodes;
	}

	private async Task<List<TestNode>> DiscoverTestsForProject(SharpIdeProjectModel project)
	{
		using var client = await GetInitialisedClientAsync(project);
		List<TestNodeUpdate> testNodeUpdates = [];
		var discoveryResponse = await client.DiscoverTestsAsync(Guid.NewGuid(), node =>
		{
			testNodeUpdates.AddRange(node);
			return Task.CompletedTask;
		});
		await discoveryResponse.WaitCompletionAsync();

		await client.ExitAsync();
		var discoveredTestNodes = testNodeUpdates.Select(x => x.Node).ToList();
		_logger.LogInformation("Discovered {DiscoveredTestCount} tests for project {ProjectName}", discoveredTestNodes.Count, project.Name.Value);
		return discoveredTestNodes;
	}

	public async Task RunTestsForSolutionAsync(SharpIdeSolutionModel solutionModel, Func<TestNodeUpdate[], Task> func)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		foreach (var testProject in testProjects)
		{
			await RunTestsForProjectAsync(testProject, func);
		}
	}

	// Assumes it has already been built
	private async Task RunTestsForProjectAsync(SharpIdeProjectModel project, Func<TestNodeUpdate[], Task> func)
	{
		using var client = await GetInitialisedClientAsync(project);
		List<TestNodeUpdate> testNodeUpdates = [];
		var discoveryResponse = await client.DiscoverTestsAsync(Guid.NewGuid(), async nodeUpdates =>
		{
			testNodeUpdates.AddRange(nodeUpdates);
			await func(nodeUpdates);
		});
		await discoveryResponse.WaitCompletionAsync();

		ResponseListener runRequest = await client.RunTestsAsync(Guid.NewGuid(), testNodeUpdates.Select(x => x.Node).ToArray(), func);
		await runRequest.WaitCompletionAsync();
		await client.ExitAsync();
	}

	private async Task<TestingPlatformClient> GetInitialisedClientAsync(SharpIdeProjectModel project)
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
