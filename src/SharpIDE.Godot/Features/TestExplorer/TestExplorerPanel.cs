using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Testing;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestExplorerPanel : Control
{
    [Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;
    [Inject] private readonly TestRunnerService _testRunnerService = null!;
    [Inject] private readonly BuildService _buildService = null!;
    
    private readonly PackedScene _testNodeEntryScene = ResourceLoader.Load<PackedScene>("uid://dt50f2of66dlt");

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
        var solution = _solutionAccessor.SolutionModel!;
        if (withBuild)
        {
            await _buildService.MsBuildAsync(solution.FilePath, buildStartedFlags: BuildStartedFlags.Internal);
        }
        var testNodes = await _testRunnerService.DiscoverTests(solution);
        var scenes = testNodes.Select(s =>
        {
            var entry = _testNodeEntryScene.Instantiate<TestNodeEntry>();
            entry.TestNode = s;
            return entry;
        });
        await this.InvokeAsync(() =>
        {
            _testNodesVBoxContainer.QueueFreeChildren();
            foreach (var scene in scenes)
            {
                _testNodesVBoxContainer.AddChild(scene);
            }
        });
    }

    private readonly Dictionary<string, TestNodeEntry> _testNodeEntryNodes = [];
    private void OnRunAllTestsButtonPressed()
    {
        _ = Task.GodotRun(async () =>
        {
            await _solutionAccessor.SolutionReadyTcs.Task;
            var solution = _solutionAccessor.SolutionModel!;
            await _buildService.MsBuildAsync(solution.FilePath, buildStartedFlags: BuildStartedFlags.Internal);
            await this.InvokeAsync(() => _testNodesVBoxContainer.QueueFreeChildren());
            _testNodeEntryNodes.Clear();
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
                if (_testNodeEntryNodes.TryGetValue(update.Node.Uid, out var entry))
                {
                    entry.TestNode = update.Node;
                    entry.SetValues();
                }
                else
                {
                    var newEntry = _testNodeEntryScene.Instantiate<TestNodeEntry>();
                    newEntry.TestNode = update.Node;
                    _testNodeEntryNodes[update.Node.Uid] = newEntry;
                    _testNodesVBoxContainer.AddChild(newEntry);
                }
            }
        });
    }
}