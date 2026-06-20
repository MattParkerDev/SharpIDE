using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Testing;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestExplorerPanel : Control
{
	private Button _refreshButton = null!;
	private Tree _testNodesTree = null!;
	private Button _runAllTestsButton = null!;

	private readonly Dictionary<string, TreeItem> _testNodeTreeItems = [];

	[Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;
	[Inject] private readonly TestRunnerService _testRunnerService = null!;
	[Inject] private readonly BuildService _buildService = null!;

	public override void _Ready()
	{
		_refreshButton = GetNode<Button>("%RefreshButton");
		_testNodesTree = GetNode<Tree>("%TestNodesTree");
		_runAllTestsButton = GetNode<Button>("%RunAllTestsButton");
		_testNodeCustomDrawCallable = new Callable(this, MethodName.TestNodeCustomDraw);
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
		var testNodes = await _testRunnerService.DiscoverTests(solution);

		_testNodeTreeItems.Clear();
		await this.InvokeAsync(() =>
		{
			_testNodesTree.Clear();
			var root = _testNodesTree.CreateItem();

			foreach (var testNode in testNodes)
			{
				var treeItem = root.CreateChild();
				UpdateTestNodeTreeItem(treeItem, testNode);
				_testNodeTreeItems[testNode.Uid] = treeItem;
			}
		});
	}

	private void UpdateTestNodeTreeItem(TreeItem treeItem, TestNode testNode)
	{
		treeItem.SetCellMode(0, TreeItem.TreeCellMode.Custom);
		treeItem.SetCustomAsButton(0, true);
		treeItem.SetTooltipText(0, testNode.DisplayName);
		treeItem.SharpIdeTestNode = testNode;
		// Avoid allocation via Callable.From((TreeItem s, Rect2 x) => CustomDraw(s, x))
		treeItem.SetCustomDrawCallback(0, _testNodeCustomDrawCallable!.Value);
	}

	private void OnRunAllTestsButtonPressed()
	{
		_ = Task.GodotRun(async () =>
		{
			await _solutionAccessor.SolutionReadyTcs.Task;
			var solution = _solutionAccessor.SolutionModel;
			await _buildService.MsBuildAsync(solution.FilePath, buildStartedFlags: BuildStartedFlags.Internal);
			await this.InvokeAsync(() =>
			{
				_testNodesTree.Clear();
				_testNodesTree.CreateItem(); // create a new root
			});
			_testNodeTreeItems.Clear();
			await _testRunnerService.RunTestsAsync(solution, HandleTestNodeUpdates);
		});
	}

	private async Task HandleTestNodeUpdates(TestNodeUpdate[] nodeUpdates)
	{
		// Receive node updates - could be discovery, running, success, failed, skipped, etc
		await this.InvokeAsync(() =>
		{
			foreach (var update in nodeUpdates)
			{
				if (_testNodeTreeItems.TryGetValue(update.Node.Uid, out var treeItem))
				{
					UpdateTestNodeTreeItem(treeItem, update.Node);
				}
				else
				{
					var newTreeItem = _testNodesTree.GetRoot().CreateChild();
					UpdateTestNodeTreeItem(newTreeItem, update.Node);
					_testNodeTreeItems[update.Node.Uid] = newTreeItem;
				}
			}
		});
	}
}
