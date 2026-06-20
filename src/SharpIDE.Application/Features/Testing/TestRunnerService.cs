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

	public async Task DiscoverTestsForSolution(SharpIdeSolutionModel solutionModel, Func<TestNodeUpdate[], Task> func)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		var discoveredNodeCount = 0;
		foreach (var testProject in testProjects)
		{
			await using var client = await GetInitialisedClient(testProject);
			var testNodes = await DiscoverTestsForProject(client, testProject, func);
			discoveredNodeCount += testNodes.Length;
		}
		_logger.LogInformation("Discovered {DiscoveredTestCount} tests", discoveredNodeCount);
	}

	private async Task<TestNode[]> DiscoverTestsForProject(TestingPlatformClient clientForProject, SharpIdeProjectModel project, Func<TestNodeUpdate[], Task> func)
	{
		List<TestNodeUpdate> testNodeUpdates = [];
		var discoveryResponse = await clientForProject.DiscoverTestsAsync(Guid.NewGuid(), async nodeUpdates =>
		{
			testNodeUpdates.AddRange(nodeUpdates);
			await func(nodeUpdates);
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

		var sessions = new List<(TestingPlatformClient Client, SharpIdeProjectModel Project, TestNode[] Nodes)>();
		try
		{
			// Run all discovery first, so that the UI is populated with every test
			foreach (var testProject in testProjects)
			{
				var client = await GetInitialisedClient(testProject);
				var discoveredTestNodes = await DiscoverTestsForProject(client, testProject, func);
				sessions.Add((client, testProject, discoveredTestNodes));
			}

			foreach (var (client, project, nodes) in sessions)
			{
				await RunTestsForProject(client, project, nodes, func);
				await client.DisposeAsync();
			}
		}
		finally
		{
			foreach (var (client, _, _) in sessions) await client.DisposeAsync();
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
