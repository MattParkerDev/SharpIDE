using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.Testing;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestExplorerPanel : Control
{
	[Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;
	[Inject] private readonly TestRunnerService _testRunnerService = null!;
	[Inject] private readonly BuildService _buildService = null!;

	private readonly PackedScene _testNodeEntryScene = ResourceLoader.Load<PackedScene>("uid://dt50f2of66dlt");
	private readonly Dictionary<string, TestNodeEntry> _testNodeEntryNodes = [];
	private readonly HashSet<string> _collapsedGroupUids = [];

	private Button _refreshButton = null!;
	private VBoxContainer _testNodesVBoxContainer = null!;
	private Button _runAllTestsButton = null!;

	public override void _Ready()
	{
		_refreshButton = GetNode<Button>("%RefreshButton");
		_testNodesVBoxContainer = GetNode<VBoxContainer>("%TestNodesVBoxContainer");
		_runAllTestsButton = GetNode<Button>("%RunAllTestsButton");
		_ = Task.GodotRun(AsyncReady);
		_refreshButton.Pressed += OnRefreshButtonPressed;
		_runAllTestsButton.Pressed += OnRunAllTestsButtonPressed;
	}

	private async Task AsyncReady()
	{
		// Until this is finished/optimised to handle lots of tests, require manual refresh
		//await DiscoverTestNodesForSolution(false);
	}

	private void OnRefreshButtonPressed()
	{
		_ = Task.GodotRun(() => DiscoverTestNodesForSolution(true));
	}

	private async Task DiscoverTestNodesForSolution(bool withBuild)
	{
		await _solutionAccessor.SolutionReadyTcs.Task;
		var solution = _solutionAccessor.SolutionModel;
		if (withBuild)
		{
			await _buildService.MsBuildAsync(solution.FilePath, buildStartedFlags: BuildStartedFlags.Internal);
		}
		var testNodes = await _testRunnerService.DiscoverTestHierarchy(solution);
		using var scenes = testNodes.AsValueEnumerable().Select(CreateTestNodeEntry).ToArrayPool();
		await this.InvokeAsync(() =>
		{
			_testNodesVBoxContainer.QueueFreeChildren();
			_testNodeEntryNodes.Clear();
			foreach (var scene in scenes.Span)
			{
				_testNodeEntryNodes[scene.TestNodeHierarchy.Uid] = scene;
				_testNodesVBoxContainer.AddChild(scene);
			}
			ApplyCollapsedState();
		});
	}

	private void OnRunAllTestsButtonPressed()
	{
		_ = Task.GodotRun(async () =>
		{
			await _solutionAccessor.SolutionReadyTcs.Task;
			var solution = _solutionAccessor.SolutionModel;
			await _buildService.MsBuildAsync(solution.FilePath, buildStartedFlags: BuildStartedFlags.Internal);
			await this.InvokeAsync(() => _testNodesVBoxContainer.QueueFreeChildren());
			_testNodeEntryNodes.Clear();
			await _testRunnerService.RunTestsAsync(solution, HandleTestNodeUpdates);
		});
	}

	private async Task HandleTestNodeUpdates(SharpIdeProjectModel testProject, TestNodeUpdate[] nodeUpdates)
	{
		// Receive node updates - could be discovery, running, success, failed, skipped, etc
		await this.InvokeAsync(() =>
		{
			foreach (var update in nodeUpdates)
			{
				foreach (var hierarchyNode in TestRunnerService.BuildTestNodeHierarchyPath(update.Node, testProject.RootNamespace))
				{
					if (_testNodeEntryNodes.TryGetValue(hierarchyNode.Uid, out var entry))
					{
						entry.TestNodeHierarchy = hierarchyNode;
						entry.SetValues();
					}
					else
					{
						var newEntry = CreateTestNodeEntry(hierarchyNode);
						_testNodeEntryNodes[hierarchyNode.Uid] = newEntry;
						_testNodesVBoxContainer.AddChild(newEntry);
					}
				}
			}
			ApplyCollapsedState();
		});
	}

	private TestNodeEntry CreateTestNodeEntry(TestNodeHierarchy hierarchyNode)
	{
		var entry = _testNodeEntryScene.Instantiate<TestNodeEntry>();
		entry.TestNodeHierarchy = hierarchyNode;
		entry.GroupClicked += OnTestNodeGroupClicked;
		return entry;
	}

	private void OnTestNodeGroupClicked(TestNodeHierarchy hierarchyNode)
	{
		if (!hierarchyNode.IsGroup)
		{
			return;
		}

		if (!_collapsedGroupUids.Add(hierarchyNode.Uid))
		{
			_collapsedGroupUids.Remove(hierarchyNode.Uid);
		}

		ApplyCollapsedState();
	}

	private void ApplyCollapsedState()
	{
		foreach (var entry in _testNodeEntryNodes.Values)
		{
			var hierarchyNode = entry.TestNodeHierarchy;
			entry.Visible = hierarchyNode?.AncestorUids.Any(_collapsedGroupUids.Contains) != true;
		}
	}
}
