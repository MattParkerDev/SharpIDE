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

	public SharpIdeCodeEdit? GetCurrentCodeEdit() => _tabContainer.GetChildOrNull<SharpIdeCodeEditContainer>(_tabContainer.CurrentTab)?.CodeEdit;

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

	private Task OnDebuggerExecutionStopped(ExecutionStopInfo executionStopInfo)
	{
		Guard.Against.Null(Solution, nameof(Solution));
		if (_debuggerExecutionStopInfoByProject.TryGetValue(executionStopInfo.Project, out _)) throw new InvalidOperationException("Debugger is already stopped for this project.");
		_debuggerExecutionStopInfoByProject[executionStopInfo.Project] = executionStopInfo;
		return Task.CompletedTask;
	}

	private async Task OnDebuggerStackFrameSelected(SharpIdeProjectModel project, StackFrameModel? stackFrame)
	{
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
				var codeEdit = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().Single(t => t.CodeEdit.SharpIdeFile == file).CodeEdit;
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
