using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.Testing.Client;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Application.Features.Testing;

public class TestRunnerService(RoslynAnalysis roslynAnalysis)
{
	private readonly RoslynAnalysis _roslynAnalysis = roslynAnalysis;

	public async Task<List<TestNodeHierarchy>> DiscoverTestHierarchy(SharpIdeSolutionModel solutionModel)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		List<TestNodeHierarchy> hierarchy = [];
		HashSet<string> addedGroupUids = [];

		foreach (var testProject in testProjects.OrderBy(p => p.RootNamespace, StringComparer.OrdinalIgnoreCase))
		{
			var nodes = await DiscoverTests(testProject);
			hierarchy.AddRange(BuildTestHierarchy(nodes, testProject.RootNamespace, addedGroupUids));
		}

		return hierarchy;
	}

	private static List<TestNodeHierarchy> BuildTestHierarchy(IEnumerable<TestNode> testNodes, string? rootNamespace, HashSet<string> addedGroupUids)
	{
		List<TestNodeHierarchy> hierarchy = [];

		foreach (var node in testNodes.OrderBy(node => GetSortKey(node, rootNamespace), StringComparer.OrdinalIgnoreCase))
		{
			foreach (var hierarchyNode in BuildTestNodeHierarchyPath(node, rootNamespace))
			{
				if (hierarchyNode.IsGroup && !addedGroupUids.Add(hierarchyNode.Uid))
				{
					continue;
				}

				hierarchy.Add(hierarchyNode);
			}
		}

		return hierarchy;
	}

	public static List<TestNodeHierarchy> BuildTestNodeHierarchyPath(TestNode node, string? rootNamespace = null)
	{
		var testNodePath = GetTestNodePath(node, rootNamespace);
		List<TestNodeHierarchy> hierarchy = [];
		List<string> namespaceParts = [];
		List<string> ancestorUids = [];

		for (var i = 0; i < testNodePath.NamespaceParts.Length; i++)
		{
			namespaceParts.Add(testNodePath.NamespaceParts[i]);
			var namespaceUid = $"namespace:{string.Join('.', namespaceParts)}";
			hierarchy.Add(new TestNodeHierarchy
			{
				Uid = namespaceUid,
				DisplayName = testNodePath.NamespaceParts[i],
				Kind = TestNodeHierarchyKind.Namespace,
				IndentLevel = i,
				AncestorUids = [.. ancestorUids]
			});
			ancestorUids.Add(namespaceUid);
		}

		var testIndentLevel = namespaceParts.Count;
		if (testNodePath.TypeName is not null)
		{
			var typeUid = $"type:{string.Join('.', namespaceParts.Append(testNodePath.TypeName))}";
			hierarchy.Add(new TestNodeHierarchy
			{
				Uid = typeUid,
				DisplayName = testNodePath.TypeName,
				Kind = TestNodeHierarchyKind.Type,
				IndentLevel = namespaceParts.Count,
				AncestorUids = [.. ancestorUids]
			});
			ancestorUids.Add(typeUid);
			testIndentLevel++;
		}

		hierarchy.Add(new TestNodeHierarchy
		{
			Uid = node.Uid,
			DisplayName = testNodePath.TestName,
			Kind = TestNodeHierarchyKind.Test,
			IndentLevel = testIndentLevel,
			AncestorUids = [.. ancestorUids],
			TestNode = node
		});

		return hierarchy;
	}

	public async Task RunTestsAsync(SharpIdeSolutionModel solutionModel, Func<SharpIdeProjectModel, TestNodeUpdate[], Task> func)
	{
		await Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask));
		var testProjects = solutionModel.AllProjects.Where(p => p.IsMtpTestProject).ToList();
		foreach (var testProject in testProjects)
		{
			await RunTestsAsync(testProject, nodeUpdates => func(testProject, nodeUpdates));
		}
	}

	// Assumes it has already been built
	public async Task RunTestsAsync(SharpIdeProjectModel project, Func<TestNodeUpdate[], Task> func)
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

	private async Task<List<TestNode>> DiscoverTests(SharpIdeProjectModel testProject)
	{
		using var client = await GetInitialisedClientAsync(testProject);
		List<TestNodeUpdate> testNodeUpdates = [];
		var discoveryResponse = await client.DiscoverTestsAsync(Guid.NewGuid(), node =>
		{
			testNodeUpdates.AddRange(node);
			return Task.CompletedTask;
		});
		await discoveryResponse.WaitCompletionAsync();

		await client.ExitAsync();
		return testNodeUpdates.Select(testNodeUpdate => testNodeUpdate.Node).ToList();
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

	private static string GetSortKey(TestNode node, string? rootNamespace)
	{
		var testNodePath = GetTestNodePath(node, rootNamespace);
		var namespacePrefix = testNodePath.NamespaceParts.Length > 0 ? string.Join('.', testNodePath.NamespaceParts) + "." : string.Empty;
		var typePrefix = testNodePath.TypeName is not null ? testNodePath.TypeName + "." : string.Empty;
		return $"{namespacePrefix}{typePrefix}{testNodePath.TestName}";
	}

	private static TestNodePath GetTestNodePath(TestNode node, string? rootNamespace)
	{
		if (!string.IsNullOrWhiteSpace(node.LocationType))
		{
			var typeParts = node.LocationType.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var typeName = typeParts.Length > 0 ? typeParts[^1] : null;
			var namespaceParts = typeParts.Length > 1 ? typeParts[..^1] : [];
			return new TestNodePath(AddRootNamespace(namespaceParts, rootNamespace), typeName, GetTestDisplayName(node));
		}

		var displayName = GetTestDisplayName(node);
		var displayNameParts = displayName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (displayNameParts.Length <= 1)
		{
			return new TestNodePath(AddRootNamespace([], rootNamespace), null, displayName);
		}

		return new TestNodePath(AddRootNamespace(displayNameParts[..^1], rootNamespace), null, displayNameParts[^1]);
	}

	private static string GetTestDisplayName(TestNode node)
		=> !string.IsNullOrWhiteSpace(node.LocationMethod)
			? node.LocationMethod
			: !string.IsNullOrWhiteSpace(node.DisplayName)
				? node.DisplayName
				: node.Uid;

	private static string[] AddRootNamespace(string[] namespaceParts, string? rootNamespace)
	{
		if (string.IsNullOrWhiteSpace(rootNamespace))
		{
			return namespaceParts;
		}

		var rootNamespaceParts = rootNamespace.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var startsWithRootNamespace = namespaceParts.Length >= rootNamespaceParts.Length
									  && namespaceParts[..rootNamespaceParts.Length].SequenceEqual(rootNamespaceParts, StringComparer.Ordinal);
		var remainingNamespaceParts = startsWithRootNamespace ? namespaceParts[rootNamespaceParts.Length..] : namespaceParts;
		return [rootNamespace, .. remainingNamespaceParts];
	}

	private sealed record TestNodePath(string[] NamespaceParts, string? TypeName, string TestName);
}
