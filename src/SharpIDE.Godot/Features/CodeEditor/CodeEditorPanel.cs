using System.Collections.Concurrent;
using System.Diagnostics;
using Ardalis.GuardClauses;
using Godot;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Threading;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.IdeSettings;
using SharpIDE.Godot.Features.Settings;
using SharpIDE.Godot.Features.SolutionExplorer;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class CodeEditorPanel : PanelContainer
{
	[Export]
	public Texture2D CsFileTexture { get; set; } = null!;
	public SharpIdeSolutionModel Solution { get; set; } = null!;
	private PackedScene _sharpIdeCodeEditScene = GD.Load<PackedScene>("res://Features/CodeEditor/SharpIdeCodeEdit.tscn");
	private TabContainer _tabContainer = null!;
	private readonly ConcurrentDictionary<SharpIdeProjectModel, ExecutionStopInfo> _debuggerExecutionStopInfoByProject = [];
	private SharpIdeProjectModel? _selectedDebuggerProject;
	private SharpIdeCodeEdit? _executingCodeEdit;
	private int? _executingLine;
	private readonly CancellationSeries _debuggerSelectionCancellationSeries = new();
	private readonly SemaphoreSlim _debuggerSelectionSemaphore = new(1, 1);
	private bool _suppressTabLoading;

	[Inject] private readonly RunService _runService = null!;
	[Inject] private readonly SharpIdeMetadataAsSourceService _sharpIdeMetadataAsSourceService = null!;
	public override void _Ready()
	{
		_tabContainer = GetNode<TabContainer>("TabContainer");
		_tabContainer.RemoveChildAndQueueFree(_tabContainer.GetChild(0)); // Remove the default tab
		_tabContainer.TabClicked += OnTabClicked;
		_tabContainer.TabChanged += OnTabChanged;
		var tabBar = _tabContainer.GetTabBar();
		tabBar.TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowAlways;
		tabBar.TabClosePressed += OnTabClosePressed;
		tabBar.TabRmbClicked += OnTabRmbClicked;
		GlobalEvents.Instance.DebuggerExecutionStopped.Subscribe(OnDebuggerExecutionStopped);
		GlobalEvents.Instance.ProjectStoppedDebugging.Subscribe(OnProjectStoppedDebugging);
		GodotGlobalEvents.Instance.DebuggerStackFrameSelected.Subscribe(OnDebuggerStackFrameSelected);
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

	public SharpIdeCodeEdit? GetCurrentCodeEdit() => (_tabContainer.GetCurrentTabControl() as CodeEditorTab)?.CodeEdit;

	private IEnumerable<CodeEditorTab> GetTabs() => _tabContainer.GetChildren().OfType<CodeEditorTab>();

	private void AdjustCodeEditorUiScale(bool increase)
	{
		const int minFontSize = 8;
		const int maxFontSize = 72;

		var currentFontSize = GetThemeFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.CodeEdit);
		var newFontSize = increase
			? Mathf.Clamp(currentFontSize + 2, minFontSize, maxFontSize)
			: Mathf.Clamp(currentFontSize - 2, minFontSize, maxFontSize);

		this.ThemeSetCodeEditFontSize(newFontSize);
	}

	public override void _ExitTree()
	{
		_debuggerSelectionCancellationSeries.CreateNext();
		var selectedTabIndex = _tabContainer.CurrentTab;
		var thisSolution = Singletons.AppState.RecentSlns.Single(s => s.FilePath == Solution.FilePath);
		thisSolution.IdeSolutionState.OpenTabs = GetTabs()
			.Select((tab, index) =>
			{
				var caretPosition = tab.GetCaretPosition();
				return new OpenTab
				{
					FilePath = tab.File.Path,
					CaretLine = caretPosition.Line,
					CaretColumn = caretPosition.Column,
					IsSelected = index == selectedTabIndex
				};
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

	private void OnTabClicked(long tabIndex)
	{
		var tab = (CodeEditorTab)_tabContainer.GetTabControl((int)tabIndex);
		GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(tab.File, tab.GetCaretPosition());
	}

	// Editors are created lazily, the first time their tab is shown
	private void OnTabChanged(long tabIndex)
	{
		if (_suppressTabLoading) return;
		if (_tabContainer.GetTabControl((int)tabIndex) is CodeEditorTab tab) _ = tab.EnsureLoaded(_sharpIdeCodeEditScene, Solution);
	}

	private void OnTabClosePressed(long tabIndex)
	{
		var tab = (CodeEditorTab)_tabContainer.GetTabControl((int)tabIndex);
		CloseTabs([tab]);
	}

	private void OnTabRmbClicked(long tabIndex)
	{
		OpenContextMenuTab(tabIndex);
	}

	private void CloseTabs(List<CodeEditorTab> tabsToClose)
	{
		var allTabs = GetTabs().ToList();
		var currentTab = (CodeEditorTab?)_tabContainer.GetCurrentTabControl();
		var closingCurrentTab = currentTab is not null && tabsToClose.Contains(currentTab);
		if (closingCurrentTab) RecordNavigationToNextSelectedTab(allTabs, tabsToClose, currentTab!);

		foreach (var tab in tabsToClose) _tabContainer.RemoveChildAndQueueFree(tab);
	}

	private void RecordNavigationToNextSelectedTab(List<CodeEditorTab> allTabs, List<CodeEditorTab> tabsToClose, CodeEditorTab currentTabToBeClosed)
	{
		var remainingTabsIncludingCurrentTab = allTabs.Except(tabsToClose.Except([currentTabToBeClosed])).ToList();
		Guard.Against.Zero(remainingTabsIncludingCurrentTab.Count);
		if (remainingTabsIncludingCurrentTab.Count is 1) return; // once the current tab is removed, there will be no tabs remaining. No navigation to do.
		if (remainingTabsIncludingCurrentTab.Count > 1)
		{
			var currentTabIndexInTempList = remainingTabsIncludingCurrentTab.IndexOf(currentTabToBeClosed);
			if (currentTabIndexInTempList is -1) throw new UnreachableException("Current tab to be closed should be in the list of remaining tabs including current tab");
			var tabToBeSelected = currentTabIndexInTempList is 0 ? remainingTabsIncludingCurrentTab[1] : remainingTabsIncludingCurrentTab[currentTabIndexInTempList - 1];

			// This isn't actually necessary - closing a tab automatically selects the previous tab, however we need to do it to select the file in sln explorer, record navigation event etc
			GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(tabToBeSelected.File, tabToBeSelected.GetCaretPosition());
		}
	}

	public async Task SetSharpIdeFile(SharpIdeFile file, SharpIdeFileLinePosition? fileLinePosition)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var (tab, wasLoaded, loadTask) = await this.InvokeAsync(() =>
		{
			var tab = GetTabs().FirstOrDefault(t => t.File == file);
			var wasLoaded = tab?.IsLoaded is true;
			if (tab is null) tab = AddTab(file, fileLinePosition);
			else if (wasLoaded is false && fileLinePosition is not null) tab.PendingLinePosition = fileLinePosition;
			_tabContainer.CurrentTab = tab.GetIndex();
			var loadTask = tab.EnsureLoaded(_sharpIdeCodeEditScene, Solution);
			return (tab, wasLoaded, loadTask);
		});
		await loadTask;
		// A freshly loaded editor has already had the position applied
		if (wasLoaded && fileLinePosition is not null)
		{
			await this.InvokeAsync(() =>
			{
				if (IsInstanceValid(tab) && tab.CodeEdit is { } codeEdit && IsInstanceValid(codeEdit)) codeEdit.SetFileLinePosition(fileLinePosition.Value);
			});
		}
	}

	/// Restores previously open tabs without creating their editors - each editor is created when its tab is first shown.
	/// The selected tab is made current, but not loaded - selecting its file (FileExternallySelected) loads it.
	public async Task RestoreTabs(IReadOnlyList<(SharpIdeFile file, SharpIdeFileLinePosition linePosition)> tabs, SharpIdeFile? selectedFile)
	{
		await this.InvokeAsync(() =>
		{
			_suppressTabLoading = true;
			try
			{
				CodeEditorTab? selectedTab = null;
				foreach (var (file, linePosition) in tabs)
				{
					if (GetTabs().Any(t => t.File == file)) continue;
					var tab = AddTab(file, linePosition);
					if (file == selectedFile) selectedTab = tab;
				}
				if (selectedTab is not null) _tabContainer.CurrentTab = selectedTab.GetIndex();
			}
			finally
			{
				_suppressTabLoading = false;
			}
		});
	}

	[RequiresGodotUiThread]
	private CodeEditorTab AddTab(SharpIdeFile file, SharpIdeFileLinePosition? linePosition)
	{
		var newTab = CodeEditorTab.Create(file, linePosition);
		_tabContainer.AddChild(newTab);
		var newTabIndex = newTab.GetIndex();
		_tabContainer.SetIconsForFileExtension(file, newTabIndex);
		_tabContainer.SetTabTitle(newTabIndex, file.Name.Value + (file.IsDirty.Value ? " (*)" : ""));
		_tabContainer.SetTabTooltip(newTabIndex, file.Path);

		file.FileDeleted.Subscribe(async () =>
		{
			await this.InvokeAsync(() =>
			{
				if (IsInstanceValid(newTab) && newTab.GetParent() == _tabContainer) CloseTabs([newTab]);
			});
		});

		var nameChanged = file.Name.Skip(1).Select(name => (name, file.IsDirty.Value));
		var dirtyChanged = file.IsDirty.Skip(1).Select(isDirty => (file.Name.Value, isDirty));

		nameChanged.Merge(dirtyChanged).SubscribeOnThreadPool().ObserveOnThreadPool()
			.SubscribeAwait(async (x, ct) =>
			{
				var (name, isDirty) = x;
				await UpdateTabFileName(newTab, name, isDirty);
			}, configureAwait: false)
			.AddTo(newTab); // needs to be on ui thread
		return newTab;
	}

	private async Task UpdateTabFileName(CodeEditorTab tab, string name, bool isDirty)
	{
		await this.InvokeAsync(() =>
		{
			if (IsInstanceValid(tab) is false) return;
			var title = name + (isDirty ? " (*)" : "");
			_tabContainer.SetTabTitle(tab.GetIndex(), title);
		});
	}

	private Task OnDebuggerExecutionStopped(ExecutionStopInfo executionStopInfo)
	{
		Guard.Against.Null(Solution, nameof(Solution));
		if (_debuggerExecutionStopInfoByProject.TryGetValue(executionStopInfo.Project, out _)) throw new InvalidOperationException("Debugger is already stopped for this project.");
		_debuggerExecutionStopInfoByProject[executionStopInfo.Project] = executionStopInfo;
		return Task.CompletedTask;
	}

	private async Task OnDebuggerStackFrameSelected(SharpIdeProjectModel project, StackFrameModel? stackFrame)
	{
		// Stopped highlights should only be shown for the currently selected debug tab's project.
		// Ignore events from other projects - switching to that project's debug tab will re-fire this event via ShowSelectedStackFrame.
		if (_selectedDebuggerProject is not null && _selectedDebuggerProject != project) return;
		var cancellationToken = _debuggerSelectionCancellationSeries.CreateNext();
		_selectedDebuggerProject = project;
		await this.InvokeAsync(ClearExecutingLine);
		if (stackFrame is null or { IsExternalCode: true } || stackFrame.Source is null || stackFrame.Line is null || stackFrame.Column is null) return;

		var ownsSelectionSemaphore = false;
		try
		{
			await _debuggerSelectionSemaphore.WaitAsync(cancellationToken);
			ownsSelectionSemaphore = true;
			SharpIdeFile? file;
			if (stackFrame.DecompiledSourceInfo is { } decompiledSourceInfo)
			{
				file = await _sharpIdeMetadataAsSourceService.CreateSharpIdeFileForMetadataAsSourceForTypeFromDebuggingAsync(decompiledSourceInfo.TypeFullName, decompiledSourceInfo.Assembly.AssemblyPath, decompiledSourceInfo.Assembly.Mvid, decompiledSourceInfo.CallingUserCodeAssemblyPath, cancellationToken);
				if (file is null) throw new InvalidOperationException($"Failed to create file for metadata as source for type {decompiledSourceInfo.TypeFullName} in assembly {decompiledSourceInfo.Assembly.AssemblyPath}.");
			}
			else
			{
				file = Solution.AllFiles.GetValueOrDefault(stackFrame.Source);
			}
			if (file is null || cancellationToken.IsCancellationRequested) return;

			var line = Math.Max(0, stackFrame.Line.Value - 1);
			var column = Math.Max(0, stackFrame.Column.Value - 1);
			await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(file, new SharpIdeFileLinePosition(line, column)).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested) return;

			await this.InvokeAsync(() =>
			{
				if (cancellationToken.IsCancellationRequested) return;
				ClearExecutingLine();
				var codeEdit = GetTabs().Single(t => t.File == file).CodeEdit;
				if (codeEdit is null) return;
				var endLine = stackFrame.EndLine ?? stackFrame.Line.Value;
				var endColumn = stackFrame.EndColumn ?? stackFrame.Column.Value;
				var endPosition = new LinePosition(Math.Max(line, endLine - 1), Math.Max(0, endColumn - 1));
				codeEdit.SetExecutingTextSpanInfo(new LinePositionSpan(new LinePosition(line, column), endPosition));
				codeEdit.SetLineAsExecuting(line, true);
				_executingCodeEdit = codeEdit;
				_executingLine = line;
			});
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A newer stack-frame selection superseded this one.
		}
		finally
		{
			if (ownsSelectionSemaphore) _debuggerSelectionSemaphore.Release();
		}
	}

	[RequiresGodotUiThread]
	private void ClearExecutingLine()
	{
		if (_executingCodeEdit is not null && _executingLine is { } executingLine && IsInstanceValid(_executingCodeEdit))
		{
			_executingCodeEdit.SetLineAsExecuting(executingLine, false);
			_executingCodeEdit.SetExecutingTextSpanInfo(null);
		}
		_executingCodeEdit = null;
		_executingLine = null;
	}

	private enum DebuggerStepAction { StepOver, StepIn, StepOut, Continue }
	[RequiresGodotUiThread]
	private void SendDebuggerStepCommand(DebuggerStepAction debuggerStepAction)
	{
		var project = _selectedDebuggerProject;
		if (project is null) return;
		if (!_debuggerExecutionStopInfoByProject.TryRemove(project, out var executionStopInfo)) return;
		_debuggerSelectionCancellationSeries.CreateNext();
		ClearExecutingLine();
		var threadId = executionStopInfo.ThreadId;
		_ = Task.GodotRun(async () =>
		{
			var task = debuggerStepAction switch
			{
				DebuggerStepAction.StepOver => _runService.SendDebuggerStepOver(project, threadId),
				DebuggerStepAction.StepIn => _runService.SendDebuggerStepInto(project, threadId),
				DebuggerStepAction.StepOut => _runService.SendDebuggerStepOut(project, threadId),
				DebuggerStepAction.Continue => _runService.SendDebuggerContinue(project, threadId),
				_ => throw new ArgumentOutOfRangeException(nameof(debuggerStepAction), debuggerStepAction, null)
			};
			await task;
		});
	}

	private async Task OnProjectStoppedDebugging(SharpIdeProjectModel project)
	{
		_debuggerExecutionStopInfoByProject.TryRemove(project, out _);
		if (_selectedDebuggerProject != project) return;
		_selectedDebuggerProject = null;
		_debuggerSelectionCancellationSeries.CreateNext();
		await this.InvokeAsync(ClearExecutingLine);
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
