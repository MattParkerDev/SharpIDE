using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.Testing.Client;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Application.Features.Testing;

public class TestRunnerService(ILogger<TestRunnerService> logger)
{
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
			foreach (var testNodeUpdate in nodeUpdates)
			{
				testNodeUpdate.Node.Project = project;
				testNodeUpdates.Add(testNodeUpdate);
			}
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
		ResponseListener runRequest = await clientForProject.RunTestsAsync(Guid.NewGuid(), testNodes, async nodeUpdates =>
		{
			foreach (var testNodeUpdate in nodeUpdates)
			{
				testNodeUpdate.Node.Project = project;
			}
			await func(nodeUpdates);
		});
		await runRequest.WaitCompletionAsync();
	}

	private async Task<TestingPlatformClient> GetInitialisedClient(SharpIdeProjectModel project)
	{
		// MTP test projects are executables, so the evaluated RunCommand/RunArguments launch them - the apphost, or dotnet exec <dll> when UseAppHost is false
		var snapshot = project.ActiveMsBuildSnapshot;
		var launch = ProjectLaunchResolver.Resolve(project.DirectoryPath, snapshot.GetPropertyValue("OutputType"), snapshot.Properties, launchProfile: null);

		var client = await TestingPlatformClientFactory.StartAsServerAndConnectToTheClientAsync(launch);
		await client.InitializeAsync();
		return client;
	}
}
