using System.Collections.Concurrent;
using System.Diagnostics;
using Ardalis.GuardClauses;
using Godot;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.IdeSettings;
using SharpIDE.Godot.Features.SolutionExplorer;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class CodeEditorPanel : MarginContainer
{
	[Export]
	public Texture2D CsFileTexture { get; set; } = null!;
	public SharpIdeSolutionModel Solution { get; set; } = null!;
	private PackedScene _sharpIdeCodeEditScene = GD.Load<PackedScene>("res://Features/CodeEditor/SharpIdeCodeEdit.tscn");
	private TabContainer _tabContainer = null!;
	private ConcurrentDictionary<SharpIdeProjectModel, ExecutionStopInfo> _debuggerExecutionStopInfoByProject = [];
	
	[Inject] private readonly RunService _runService = null!;
	[Inject] private readonly SharpIdeMetadataAsSourceService _sharpIdeMetadataAsSourceService = null!;
	public override void _Ready()
	{
		_tabContainer = GetNode<TabContainer>("TabContainer");
		_tabContainer.RemoveChildAndQueueFree(_tabContainer.GetChild(0)); // Remove the default tab
		_tabContainer.TabClicked += OnTabClicked;
		var tabBar = _tabContainer.GetTabBar();
		tabBar.TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowAlways;
		tabBar.TabClosePressed += OnTabClosePressed;
		tabBar.TabRmbClicked += OnTabRmbClicked;
		GlobalEvents.Instance.DebuggerExecutionStopped.Subscribe(OnDebuggerExecutionStopped);
		GlobalEvents.Instance.ProjectStoppedDebugging.Subscribe(OnProjectStoppedDebugging);
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (Input.IsActionPressed(InputStringNames.EditorFontSizeIncrease))
		{
			AdjustCodeEditorUiScale(true);
		}
		else if (Input.IsActionPressed(InputStringNames.EditorFontSizeDecrease))
		{
			AdjustCodeEditorUiScale(false);
		}
	}
	
	public SharpIdeCodeEdit? GetCurrentCodeEdit() => _tabContainer.GetChildOrNull<SharpIdeCodeEditContainer>(_tabContainer.CurrentTab)?.CodeEdit;

	private void AdjustCodeEditorUiScale(bool increase)
	{
		const int minFontSize = 8;
		const int maxFontSize = 72;

		var editors = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().ToList();
		if (editors.Count is 0) return;

		var currentFontSize = editors.First().CodeEdit.GetThemeFontSize(ThemeStringNames.FontSize);
		var newFontSize = increase
			? Mathf.Clamp(currentFontSize + 2, minFontSize, maxFontSize)
			: Mathf.Clamp(currentFontSize - 2, minFontSize, maxFontSize);

		foreach (var editor in editors)
		{ 
			editor.CodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, newFontSize);
		}
	}

	public override void _ExitTree()
	{
		var selectedTabIndex = _tabContainer.CurrentTab;
		var thisSolution = Singletons.AppState.RecentSlns.Single(s => s.FilePath == Solution.FilePath);
		thisSolution.IdeSolutionState.OpenTabs = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>()
			.Select(s => s.CodeEdit)
			.Select((t, index) => new OpenTab
			{
				FilePath = t.SharpIdeFile.Path,
				CaretLine = t.GetCaretLine(),
				CaretColumn = t.GetCaretColumn(),
				IsSelected = index == selectedTabIndex
			})
			.ToList();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event.IsActionPressed(InputStringNames.StepOver))
		{
			SendDebuggerStepCommand(DebuggerStepAction.StepOver);
		}
		else if (@event.IsActionPressed(InputStringNames.DebuggerStepOut))
		{
			SendDebuggerStepCommand(DebuggerStepAction.StepOut);
		}
		else if (@event.IsActionPressed(InputStringNames.DebuggerStepIn))
		{
			SendDebuggerStepCommand(DebuggerStepAction.StepIn);
		}
		else if (@event.IsActionPressed(InputStringNames.DebuggerContinue))
		{
			SendDebuggerStepCommand(DebuggerStepAction.Continue);
		}
	}

	private void OnTabClicked(long tab)
	{
		var sharpIdeCodeEdit = _tabContainer.GetChild<SharpIdeCodeEditContainer>((int)tab).CodeEdit;
		var sharpIdeFile = sharpIdeCodeEdit.SharpIdeFile;
		var caretLinePosition = new SharpIdeFileLinePosition(sharpIdeCodeEdit.GetCaretLine(), sharpIdeCodeEdit.GetCaretColumn());
		GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(sharpIdeFile, caretLinePosition);
	}

	private void OnTabClosePressed(long tabIndex)
	{
		var tab = (SharpIdeCodeEditContainer)_tabContainer.GetTabControl((int)tabIndex);
		CloseTabs([tab]);
	}

	private void OnTabRmbClicked(long tabIndex)
	{
		OpenContextMenuTab(tabIndex);
	}

	private void CloseTabs(List<SharpIdeCodeEditContainer> tabsToClose)
	{
		var allTabs = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().ToList();
		var currentTab = (SharpIdeCodeEditContainer?)_tabContainer.GetCurrentTabControl();
		var closingCurrentTab = currentTab is not null && tabsToClose.Contains(currentTab);
		if (closingCurrentTab) RecordNavigationToNextSelectedTab(allTabs, tabsToClose, currentTab!);
		
		foreach (var tab in tabsToClose) _tabContainer.RemoveChildAndQueueFree(tab);
	}

	private void RecordNavigationToNextSelectedTab(List<SharpIdeCodeEditContainer> allTabs, List<SharpIdeCodeEditContainer> tabsToClose, SharpIdeCodeEditContainer currentTabToBeClosed)
	{
		var remainingTabsIncludingCurrentTab = allTabs.Except(tabsToClose.Except([currentTabToBeClosed])).ToList();
		Guard.Against.Zero(remainingTabsIncludingCurrentTab.Count);
		if (remainingTabsIncludingCurrentTab.Count is 1) return; // once the current tab is removed, there will be no tabs remaining. No navigation to do.
		if (remainingTabsIncludingCurrentTab.Count > 1)
		{
			var currentTabIndexInTempList = remainingTabsIncludingCurrentTab.IndexOf(currentTabToBeClosed);
			if (currentTabIndexInTempList is -1) throw new UnreachableException("Current tab to be closed should be in the list of remaining tabs including current tab");
			var tabToBeSelected = currentTabIndexInTempList is 0 ? remainingTabsIncludingCurrentTab[1] : remainingTabsIncludingCurrentTab[currentTabIndexInTempList - 1];
			
			var tabToBeSelectedCodeEdit = tabToBeSelected.CodeEdit;
			var sharpIdeFile = tabToBeSelectedCodeEdit.SharpIdeFile;
			var caretLinePosition = new SharpIdeFileLinePosition(tabToBeSelectedCodeEdit.GetCaretLine(), tabToBeSelectedCodeEdit.GetCaretColumn());
			// This isn't actually necessary - closing a tab automatically selects the previous tab, however we need to do it to select the file in sln explorer, record navigation event etc
			GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(sharpIdeFile, caretLinePosition);
		}
	}

	public async Task SetSharpIdeFile(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var existingTab = await this.InvokeAsync(() => _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().FirstOrDefault(t => t.CodeEdit.SharpIdeFile == file));
		if (existingTab is not null)
		{
			var existingTabIndex = existingTab.GetIndex();
			await this.InvokeAsync(() =>
			{
				_tabContainer.CurrentTab = existingTabIndex;
				if (fileLinePosition is not null) existingTab.CodeEdit.SetFileLinePosition(fileLinePosition.Value);
			});
			return;
		}
		var newTab = _sharpIdeCodeEditScene.Instantiate<SharpIdeCodeEditContainer>();
		newTab.CodeEdit.Solution = Solution;
		await this.InvokeAsync(() =>
		{
			_tabContainer.AddChild(newTab);
			var newTabIndex = _tabContainer.GetTabCount() - 1;
			_tabContainer.SetIconsForFileExtension(file, newTabIndex);
			_tabContainer.SetTabTitle(newTabIndex, file.Name.Value);
			_tabContainer.SetTabTooltip(newTabIndex, file.Path);
			_tabContainer.CurrentTab = newTabIndex;

			file.FileDeleted.Subscribe(async () =>
			{
				await this.InvokeAsync(() =>
				{
					CloseTabs([newTab]);
				});
			});
			
			var nameChanged = file.Name.Skip(1).Select(name => (name, file.IsDirty.Value));
			var dirtyChanged = file.IsDirty.Skip(1).Select(isDirty => (file.Name.Value, isDirty));

			nameChanged.Merge(dirtyChanged).SubscribeOnThreadPool().ObserveOnThreadPool()
				.SubscribeAwait(async (x, ct) =>
				{
					var (name, isDirty) = x;
					await UpdateTabFileName(newTab.GetIndex(), name, isDirty);
				}, configureAwait: false)
				.AddTo(newTab); // needs to be on ui thread
		});
		
		await newTab.CodeEdit.SetSharpIdeFile(file, fileLinePosition);
	}

	private async Task UpdateTabFileName(int tabIndex, string name, bool isDirty)
	{
		await this.InvokeAsync(() =>
		{
			var title = name + (isDirty ? " (*)" : "");
			_tabContainer.SetTabTitle(tabIndex, title);
		});
	}
	
	private static readonly Color ExecutingLineColor = new Color("665001");
	private async Task OnDebuggerExecutionStopped(ExecutionStopInfo executionStopInfo)
	{
		Guard.Against.Null(Solution, nameof(Solution));
		
		var lineInt = executionStopInfo.Line - 1; // Debugging is 1-indexed, Godot is 0-indexed
		Guard.Against.Negative(lineInt);

		SharpIdeFile file;
		if (executionStopInfo.DecompiledSourceInfo is { } decompiledSourceInfo)
		{
			var fileFromMetadataAsSource = await _sharpIdeMetadataAsSourceService.CreateSharpIdeFileForMetadataAsSourceForTypeFromDebuggingAsync(decompiledSourceInfo.TypeFullName, decompiledSourceInfo.Assembly.AssemblyPath, decompiledSourceInfo.Assembly.Mvid, decompiledSourceInfo.CallingUserCodeAssemblyPath);
			file = fileFromMetadataAsSource ?? throw new InvalidOperationException($"Failed to create file for metadata as source for type {decompiledSourceInfo.TypeFullName} in assembly {decompiledSourceInfo.Assembly.AssemblyPath}.");
			executionStopInfo.FilePath = file.Path;
		}
		else
		{
			file = Solution.AllFiles[executionStopInfo.FilePath];
		}
		
		// A line being darkened by the caret being on that line completely obscures the executing line color, so as a "temporary" workaround, move the caret to the previous line
		// Ideally, like Rider, we would only yellow highlight the sequence point range, with the cursor line black being behind it
		var fileLinePosition = new SharpIdeFileLinePosition(lineInt is 0 ? 0 : lineInt - 1, 0);
		// Although the file may already be the selected tab, we need to also move the caret
		await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(file, fileLinePosition).ConfigureAwait(false);
		
		if (_debuggerExecutionStopInfoByProject.TryGetValue(executionStopInfo.Project, out _)) throw new InvalidOperationException("Debugger is already stopped for this project.");
		_debuggerExecutionStopInfoByProject[executionStopInfo.Project] = executionStopInfo;
		
		await this.InvokeAsync(() =>
		{
			var tabForStopInfo = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().Single(t => t.CodeEdit.SharpIdeFile.Path == executionStopInfo.FilePath).CodeEdit;
			tabForStopInfo.SetLineBackgroundColor(lineInt, ExecutingLineColor);
			tabForStopInfo.SetLineAsExecuting(lineInt, true);
		});
	}
	
	private enum DebuggerStepAction { StepOver, StepIn, StepOut, Continue }
	[RequiresGodotUiThread]
	private void SendDebuggerStepCommand(DebuggerStepAction debuggerStepAction)
	{
		// TODO: Debugging needs a rework - debugging commands should be scoped to a debug session, ie the debug panel sub-tabs
		// For now, just use the first project that is currently stopped
		var stoppedProjects = _debuggerExecutionStopInfoByProject.Keys.ToList();
		if (stoppedProjects.Count == 0) return; // ie not currently stopped anywhere
		var project = stoppedProjects[0];
		if (!_debuggerExecutionStopInfoByProject.TryRemove(project, out var executionStopInfo)) return;
		var godotLine = executionStopInfo.Line - 1;
		var tabForStopInfo = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().Single(t => t.CodeEdit.SharpIdeFile.Path == executionStopInfo.FilePath);
		tabForStopInfo.CodeEdit.SetLineAsExecuting(godotLine, false);
		tabForStopInfo.CodeEdit.SetLineColour(godotLine);
		var threadId = executionStopInfo.ThreadId;
		_ = Task.GodotRun(async () =>
		{
			var task = debuggerStepAction switch
			{
				DebuggerStepAction.StepOver => _runService.SendDebuggerStepOver(threadId),
				DebuggerStepAction.StepIn => _runService.SendDebuggerStepInto(threadId),
				DebuggerStepAction.StepOut => _runService.SendDebuggerStepOut(threadId),
				DebuggerStepAction.Continue => _runService.SendDebuggerContinue(threadId),
				_ => throw new ArgumentOutOfRangeException(nameof(debuggerStepAction), debuggerStepAction, null)
			};
			await task;
		});
	}
	
	private async Task OnProjectStoppedDebugging(SharpIdeProjectModel project)
	{
		if (!_debuggerExecutionStopInfoByProject.TryRemove(project, out var executionStopInfo)) return;
		await this.InvokeAsync(() =>
		{
			var godotLine = executionStopInfo.Line - 1;
			var tabForStopInfo = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().Single(t => t.CodeEdit.SharpIdeFile.Path == executionStopInfo.FilePath).CodeEdit;
			tabForStopInfo.SetLineAsExecuting(godotLine, false);
			tabForStopInfo.SetLineColour(godotLine);
		});
	}
}

file static class TabContainerExtensions
{
	extension(TabContainer tabContainer)
	{
		public void SetIconsForFileExtension(SharpIdeFile file, int newTabIndex)
		{
			var (icon, overlayIcon) = FileIconHelper.GetIconForFileExtension(file.Extension);
			tabContainer.SetTabIcon(newTabIndex, icon);
			
			// Unfortunately TabContainer doesn't have a SetTabIconOverlay method
			//tabContainer.SetIconOverlay(0, overlayIcon);
		}
	}
}