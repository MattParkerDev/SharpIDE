using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Testing;
using SharpIDE.Application.Features.Testing.Client;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestExplorerPanel : Control
{
    [Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;
    [Inject] private readonly TestRunnerService _testRunnerService = null!;
    [Inject] private readonly BuildService _buildService = null!;

    private Button _refreshButton = null!;
    private Tree _testNodesTree = null!;
    private Button _runAllTestsButton = null!;

    public override void _Ready()
    {
        _refreshButton = GetNode<Button>("%RefreshButton");
        _testNodesTree = GetNode<Tree>("%TestNodesTree");
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
        var solution = _solutionAccessor.SolutionModel!;
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
	    treeItem.SetText(0, testNode.DisplayName);
	    treeItem.SetText(1, testNode.ExecutionState);
	    treeItem.SetCustomColor(1, GetTextColour(testNode.ExecutionState));
	}

    private readonly Dictionary<string, TreeItem> _testNodeTreeItems = [];
    private void OnRunAllTestsButtonPressed()
    {
        _ = Task.GodotRun(async () =>
        {
            await _solutionAccessor.SolutionReadyTcs.Task;
            var solution = _solutionAccessor.SolutionModel!;
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

    private static Color GetTextColour(string executionState)
    {
	    var colour = executionState switch
	    {
		    ExecutionStates.Passed => SuccessTextColour,
		    ExecutionStates.InProgress => RunningTextColour,
		    ExecutionStates.Discovered => PendingTextColour,
		    ExecutionStates.Failed => FailedTextColour,
		    ExecutionStates.Cancelled => CancelledTextColour,
		    ExecutionStates.Skipped => SkippedTextColour,
		    _ => Colors.White,
	    };
	    return colour;
    }

    private static readonly Color SuccessTextColour = new Color("499c54");
    private static readonly Color RunningTextColour = new Color("a77fd2");
    private static readonly Color PendingTextColour = new Color("2aa9e7");
    private static readonly Color FailedTextColour = new Color("c65344");
    private static readonly Color CancelledTextColour = new Color("e4a631");
    private static readonly Color SkippedTextColour = new Color("c0c0c0");
}
