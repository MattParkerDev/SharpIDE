using System.Collections.Concurrent;
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
	private TextureRect? _backgroundTextureRect;
	
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
		GlobalEvents.Instance.DebuggerExecutionStopped.Subscribe(OnDebuggerExecutionStopped);
		GlobalEvents.Instance.ProjectStoppedDebugging.Subscribe(OnProjectStoppedDebugging);
		GodotGlobalEvents.Instance.BackgroundImageChanged.Subscribe(OnBackgroundImageChanged);
		GodotGlobalEvents.Instance.BackgroundTransparencyChanged.Subscribe(OnBackgroundTransparencyChanged);
		InitializeBackgroundImage();
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
		var tab = _tabContainer.GetChild<Control>((int)tabIndex);
		var previousSibling = _tabContainer.GetChildOrNull<SharpIdeCodeEditContainer>((int)tabIndex - 1)?.CodeEdit;
		if (previousSibling is not null)
		{
			var sharpIdeFile = previousSibling.SharpIdeFile;
			var caretLinePosition = new SharpIdeFileLinePosition(previousSibling.GetCaretLine(), previousSibling.GetCaretColumn());
			// This isn't actually necessary - closing a tab automatically selects the previous tab, however we need to do it to select the file in sln explorer, record navigation event etc
			GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(sharpIdeFile, caretLinePosition);
		}
		_tabContainer.RemoveChild(tab);
		tab.QueueFree();
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
			
			file.IsDirty.Skip(1).SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (isDirty, ct) =>
			{
				//GD.Print($"File dirty state changed: {file.Path} is now {(isDirty ? "dirty" : "clean")}");
				await this.InvokeAsync(() =>
				{
					var tabIndex = newTab.GetIndex();
					var title = file.Name.Value + (isDirty ? " (*)" : "");
					_tabContainer.SetTabTitle(tabIndex, title);
				});
			}).AddTo(newTab); // needs to be on ui thread
		});
		
		await newTab.CodeEdit.SetSharpIdeFile(file, fileLinePosition);
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

	private void InitializeBackgroundImage()
	{
		var imagePath = Singletons.AppState.IdeSettings.BackgroundImagePath;
		if (string.IsNullOrEmpty(imagePath)) return;

		UpdateBackgroundImage(imagePath);
	}

	private Task OnBackgroundImageChanged(string imagePath)
	{
		UpdateBackgroundImage(imagePath);
		return Task.CompletedTask;
	}

	private void UpdateBackgroundImage(string imagePath)
	{
		// Remove existing background if any
		if (_backgroundTextureRect != null)
		{
			RemoveChild(_backgroundTextureRect);
			_backgroundTextureRect.QueueFree();
			_backgroundTextureRect = null;
		}

		if (string.IsNullOrEmpty(imagePath)) return;

		// Load the texture
		Texture2D? tex = null;
		if (File.Exists(imagePath))
		{
			var image = Image.LoadFromFile(imagePath);
			if (image != null)
			{
				tex = ImageTexture.CreateFromImage(image);
			}
		}

		if (tex == null) return;

		_backgroundTextureRect = new TextureRect
		{
			Texture = tex,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
			MouseFilter = MouseFilterEnum.Ignore
		};

		AddChild(_backgroundTextureRect);
		MoveChild(_backgroundTextureRect, 0); // Move behind TabContainer
		
		UpdateBackgroundTransparency(Singletons.AppState.IdeSettings.BackgroundImageTransparency);
	}

	private Task OnBackgroundTransparencyChanged(double transparency)
	{
		UpdateBackgroundTransparency(transparency);
		return Task.CompletedTask;
	}

	private void UpdateBackgroundTransparency(double transparency)
	{
		if (_backgroundTextureRect == null) return;

		var color = _backgroundTextureRect.Modulate;
		color.A = 1 - (float)transparency;
		_backgroundTextureRect.Modulate = color;
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