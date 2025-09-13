using Godot;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Run;

public partial class RunPanel : Control
{
	private TabBar _tabBar = null!;
	private MarginContainer _tabsPanel = null!;
	
	[Export]
	public Texture2D RunningIcon { get; set; } = null!;
	
	private PackedScene _runPanelTabScene = GD.Load<PackedScene>("res://Features/Run/RunPanelTab.tscn");
	public override void _Ready()
	{
		if (RunningIcon is null) throw new Exception("RunningIcon is null in RunPanel");
		_tabBar = GetNode<TabBar>("%TabBar");
		_tabBar.ClearTabs();
		//_tabBar.TabClosePressed
		_tabBar.TabClicked += OnTabBarTabClicked;
		_tabsPanel = GetNode<MarginContainer>("%TabsPanel");
		GlobalEvents.ProjectStartedRunning += async projectModel =>
		{
			await this.InvokeAsync(() => ProjectStartedRunning(projectModel));
		};
		GlobalEvents.ProjectStoppedRunning += async projectModel =>
		{
			await this.InvokeAsync(() => ProjectStoppedRunning(projectModel));
		};
	}

	private void OnTabBarTabClicked(long idx)
	{
		var children = _tabsPanel.GetChildren().OfType<RunPanelTab>().ToList();
		foreach (var child in children)
		{
			child.Visible = false;
		}

		var tab = children.Single(t => t.TabBarTab == idx);
		tab.Visible = true;
	}

	public void ProjectStartedRunning(SharpIdeProjectModel projectModel)
	{
		var existingRunPanelTab = _tabsPanel.GetChildren().OfType<RunPanelTab>().SingleOrDefault(s => s.Project == projectModel);
		if (existingRunPanelTab != null)
		{
			_tabBar.SetTabIcon(existingRunPanelTab.TabBarTab, RunningIcon);
			_tabBar.CurrentTab = existingRunPanelTab.TabBarTab;
			OnTabBarTabClicked(existingRunPanelTab.TabBarTab);
			existingRunPanelTab.ClearTerminal();
			existingRunPanelTab.StartWritingFromProjectOutput();
			return;
		}
		
		var runPanelTab = _runPanelTabScene.Instantiate<RunPanelTab>();
		runPanelTab.Project = projectModel;
		_tabBar.AddTab(projectModel.Name);
		var tabIdx = _tabBar.GetTabCount() - 1;
		runPanelTab.TabBarTab = tabIdx;
		_tabBar.SetTabIcon(runPanelTab.TabBarTab, RunningIcon);
		_tabBar.CurrentTab = runPanelTab.TabBarTab;
		_tabsPanel.AddChild(runPanelTab);
		OnTabBarTabClicked(runPanelTab.TabBarTab);
		runPanelTab.StartWritingFromProjectOutput();
	}
	
	public void ProjectStoppedRunning(SharpIdeProjectModel projectModel)
	{
		var runPanelTab = _tabsPanel.GetChildren().OfType<RunPanelTab>().Single(s => s.Project == projectModel);
		_tabBar.SetTabIcon(runPanelTab.TabBarTab, null);
	}
}