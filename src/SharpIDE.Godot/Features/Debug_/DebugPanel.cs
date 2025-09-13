using Godot;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Debug_.Tab;

namespace SharpIDE.Godot.Features.Debug_;

public partial class DebugPanel : Control
{
    private TabBar _tabBar = null!;
	private MarginContainer _tabsPanel = null!;
	
	[Export]
	public Texture2D RunningIcon { get; set; } = null!;
	
	private PackedScene _debugPanelTabScene = GD.Load<PackedScene>("res://Features/Debug_/Tab/DebugPanelTab.tscn");
	public override void _Ready()
	{
		if (RunningIcon is null) throw new Exception("RunningIcon is null in DebugPanel");
		_tabBar = GetNode<TabBar>("%TabBar");
		_tabBar.ClearTabs();
		//_tabBar.TabClosePressed
		_tabBar.TabClicked += OnTabBarTabClicked;
		_tabsPanel = GetNode<MarginContainer>("%TabsPanel");
		GlobalEvents.ProjectStartedDebugging += async projectModel =>
		{
			await this.InvokeAsync(() => ProjectStartedDebugging(projectModel));
		};
		GlobalEvents.ProjectStoppedDebugging += async projectModel =>
		{
			await this.InvokeAsync(() => ProjectStoppedDebugging(projectModel));
		};
	}

	private void OnTabBarTabClicked(long idx)
	{
		var children = _tabsPanel.GetChildren().OfType<DebugPanelTab>().ToList();
		foreach (var child in children)
		{
			child.Visible = false;
		}

		var tab = children.Single(t => t.TabBarTab == idx);
		tab.Visible = true;
	}

	public void ProjectStartedDebugging(SharpIdeProjectModel projectModel)
	{
		var existingRunPanelTab = _tabsPanel.GetChildren().OfType<DebugPanelTab>().SingleOrDefault(s => s.Project == projectModel);
		if (existingRunPanelTab != null)
		{
			_tabBar.SetTabIcon(existingRunPanelTab.TabBarTab, RunningIcon);
			_tabBar.CurrentTab = existingRunPanelTab.TabBarTab;
			OnTabBarTabClicked(existingRunPanelTab.TabBarTab);
			existingRunPanelTab.ClearTerminal();
			existingRunPanelTab.StartWritingFromProjectOutput();
			return;
		}
		
		var debugPanelTab = _debugPanelTabScene.Instantiate<DebugPanelTab>();
		debugPanelTab.Project = projectModel;
		_tabBar.AddTab(projectModel.Name);
		var tabIdx = _tabBar.GetTabCount() - 1;
		debugPanelTab.TabBarTab = tabIdx;
		_tabBar.SetTabIcon(debugPanelTab.TabBarTab, RunningIcon);
		_tabBar.CurrentTab = debugPanelTab.TabBarTab;
		_tabsPanel.AddChild(debugPanelTab);
		OnTabBarTabClicked(debugPanelTab.TabBarTab);
		debugPanelTab.StartWritingFromProjectOutput();
	}
	
	public void ProjectStoppedDebugging(SharpIdeProjectModel projectModel)
	{
		var debugPanelTab = _tabsPanel.GetChildren().OfType<DebugPanelTab>().Single(s => s.Project == projectModel);
		_tabBar.SetTabIcon(debugPanelTab.TabBarTab, null);
	}
}