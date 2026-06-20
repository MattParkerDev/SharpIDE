using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.Testing;
using SharpIDE.Application.Features.Testing.Client.Dtos;
using SharpIDE.Godot.Features.SolutionExplorer;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestExplorerPanel : Control
{
	private Button _refreshButton = null!;
	private Tree _testNodesTree = null!;
	private Button _runAllTestsButton = null!;

    private readonly Texture2D _namespaceIcon = ResourceLoader.Load<Texture2D>("uid://bob5blfjll4h3");
    private readonly Texture2D _csharpClassIcon = ResourceLoader.Load<Texture2D>("uid://b027uufaewitj");

	private readonly Dictionary<string, TreeItem> _testNodeTreeItems = [];
	private readonly Dictionary<SharpIdeProjectModel, TreeItem> _projectTreeItems = [];

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
		await this.InvokeAsync(() =>
		{
			_testNodesTree.Clear();
			_testNodesTree.CreateItem(); // create a new root
		});
		_projectTreeItems.Clear();
		_testNodeTreeItems.Clear();
		await _testRunnerService.DiscoverTestsForSolution(solution, HandleTestNodeUpdates);
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
			_projectTreeItems.Clear();
			_testNodeTreeItems.Clear();
			await _testRunnerService.RunTestsForSolution(solution, HandleTestNodeUpdates);
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
					var newTreeItem = CreateTestNodeTreeItem(update.Node);
					_testNodeTreeItems[update.Node.Uid] = newTreeItem;
				}
			}
		});
	}

	private TreeItem CreateTestNodeTreeItem(TestNode testNode)
	{
		var project = testNode.Project;

		var projectTreeItem = _projectTreeItems.GetValueOrDefault(project!);
		if (projectTreeItem is null)
		{
			projectTreeItem = _testNodesTree.GetRoot().CreateChild();
			projectTreeItem.SetText(0, project!.Name.Value);
			projectTreeItem.SetIcon(0, FileIconHelper.CsprojIcon);
			_projectTreeItems[project] = projectTreeItem;
		}
		Guard.Against.Null(projectTreeItem);

		TreeItem? classTreeItem = null;
		TreeItem? parentOfClassTreeItem = null;

		// Could be e.g. 'TestProject.UnitTest1+NestedClassTests' or 'TestProject.UnitTest1' or 'TestProject.SomeNamespace.UnitTests1'
		var fullyQualifiedClass = testNode.LocationType.AsSpan();
		var lastDotIndex = fullyQualifiedClass.LastIndexOf('.');
		if (lastDotIndex is not -1)
		{
			var namespaceName = fullyQualifiedClass[..lastDotIndex];
			var parentTreeItem = projectTreeItem;
			foreach (var range in namespaceName.Split('.'))
			{
				var namespaceSegment = namespaceName[range];
				var namespaceSegmentTreeItem = parentTreeItem.GetFirstChildWithName(ref namespaceSegment);
				if (namespaceSegmentTreeItem is null)
				{
					namespaceSegmentTreeItem = parentTreeItem.CreateChild();
					namespaceSegmentTreeItem.SetText(0, namespaceSegment.ToString());
					namespaceSegmentTreeItem.SetIcon(0, _namespaceIcon);
					namespaceSegmentTreeItem.SetIconMaxWidth(0, 20);
				}
				parentTreeItem = namespaceSegmentTreeItem;
			}
			parentOfClassTreeItem = parentTreeItem;
		}
		parentOfClassTreeItem ??= projectTreeItem;

		// className might have + in it, indicating nested class
		var className = fullyQualifiedClass[(lastDotIndex + 1)..];
		classTreeItem = parentOfClassTreeItem.GetFirstChildWithName(ref className);
		if (classTreeItem is null)
		{
			classTreeItem = parentOfClassTreeItem.CreateChild();
			classTreeItem.SetText(0, className.ToString());
			classTreeItem.SetIcon(0, _csharpClassIcon);
			classTreeItem.SetIconMaxWidth(0, 20);
		}

		Guard.Against.Null(classTreeItem);
		var newTreeItem = classTreeItem.CreateChild();
		UpdateTestNodeTreeItem(newTreeItem, testNode);
		return newTreeItem;
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
}
