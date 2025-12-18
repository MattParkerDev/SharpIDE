using Ardalis.GuardClauses;
using Godot;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Debug_.Tab.SubTabs;

public partial class ThreadsVariablesSubTab : Control
{
	private PackedScene _threadListItemScene = GD.Load<PackedScene>("res://Features/Debug_/Tab/SubTabs/ThreadListItem.tscn");

	private readonly Texture2D _fieldIcon = ResourceLoader.Load<Texture2D>("uid://c4y7d5m4upfju");
	private readonly Texture2D _propertyIcon = ResourceLoader.Load<Texture2D>("uid://y5pwrwwrjqmc");
	private readonly Texture2D _staticMembersIcon = ResourceLoader.Load<Texture2D>("uid://dudntp20myuxb");
	
	private Tree _threadsTree = null!;
	private Tree _stackFramesTree = null!;
	private Tree _variablesTree = null!;
	
	public SharpIdeProjectModel Project { get; set; } = null!;
	// private ThreadModel? _selectedThread = null!; // null when not at a stop point
	
    [Inject] private readonly RunService _runService = null!;
    
    private Callable? _debuggerVariableCustomDrawCallable;
    private readonly Dictionary<TreeItem, Variable> _variableReferenceLookup = []; // primarily used for DebuggerVariableCustomDraw

	public override void _Ready()
	{
		_threadsTree = GetNode<Tree>("%ThreadsTree");
		_stackFramesTree = GetNode<Tree>("%StackFramesTree");
		_variablesTree = GetNode<Tree>("%VariablesTree");
		_debuggerVariableCustomDrawCallable = new Callable(this, MethodName.DebuggerVariableCustomDraw);
		GlobalEvents.Instance.DebuggerExecutionStopped.Subscribe(OnDebuggerExecutionStopped);
		GlobalEvents.Instance.DebuggerExecutionContinued.Subscribe(ClearAllTrees);
		_threadsTree.ItemSelected += OnThreadSelected;
		_stackFramesTree.ItemSelected += OnStackFrameSelected;
		_variablesTree.ItemCollapsed += OnVariablesItemExpandedOrCollapsed;
		Project.ProjectStoppedRunning.Subscribe(ClearAllTrees);
	}

	private void OnVariablesItemExpandedOrCollapsed(TreeItem item)
	{
		var wasExpanded = item.IsCollapsed() is false;
		var metadata = item.GetMetadata(0).AsVector2I();
		var alreadyRetrievedChildren = metadata.X == 1;
		if (wasExpanded && alreadyRetrievedChildren is false)
		{
			// retrieve children
			var variablesReferenceId = metadata.Y;
			_ = Task.GodotRun(async () =>
			{
				var variables = await _runService.GetVariablesForVariablesReference(variablesReferenceId);
				await this.InvokeAsync(() =>
				{
					var placeholderLoadingChild = item.GetFirstChild();
					Guard.Against.Null(placeholderLoadingChild);
					placeholderLoadingChild.Visible = false; // Set to visible false rather than RemoveChild, so we don't have to Free
					foreach (var variable in variables)
					{
						AddVariableToTreeItem(item, variable);
					}
					// mark as retrieved
					item.SetMetadata(0, new Vector2I(1, variablesReferenceId));
				});
			});
		}
	}

	public override void _ExitTree()
	{
		GlobalEvents.Instance.DebuggerExecutionStopped.Unsubscribe(OnDebuggerExecutionStopped);
		GlobalEvents.Instance.DebuggerExecutionContinued.Unsubscribe(ClearAllTrees);
		Project.ProjectStoppedRunning.Unsubscribe(ClearAllTrees);
	}

	private async Task ClearAllTrees()
	{
		await this.InvokeAsync(() =>
		{
			_threadsTree.Clear();
			_stackFramesTree.Clear();
			_variablesTree.Clear();
			_variableReferenceLookup.Clear();
		});
	}

	private async void OnThreadSelected()
	{
		var selectedItem = _threadsTree.GetSelected();
		Guard.Against.Null(selectedItem);
		var threadId = selectedItem.GetMetadata(0).AsInt32();
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var stackFrames = await _runService.GetStackFrames(threadId);
		await this.InvokeAsync(() =>
		{
			_variablesTree.Clear(); // If we select a thread that does not have stack frames, the variables would not be cleared otherwise
			_stackFramesTree.Clear();
			var root = _stackFramesTree.CreateItem();
			foreach (var (index, s) in stackFrames.Index())
			{
				var stackFrameItem = _stackFramesTree.CreateItem(root);
				if (s.IsExternalCode)
				{
					stackFrameItem.SetText(0, "[External Code]");
				}
				else
				{
					// for now, just use the raw name
					stackFrameItem.SetText(0, s.Name);
					//var managedFrameInfo = s.ManagedInfo!.Value;
					//stackFrameItem.SetText(0, $"{managedFrameInfo.ClassName}.{managedFrameInfo.MethodName}() in {managedFrameInfo.Namespace}, {managedFrameInfo.AssemblyName}");
				}
				stackFrameItem.SetMetadata(0, s.Id);
				if (index is 0) _stackFramesTree.SetSelected(stackFrameItem, 0);
			}
		});
	}
	
	private async void OnStackFrameSelected()
	{
		var selectedItem = _stackFramesTree.GetSelected();
		Guard.Against.Null(selectedItem);
		var frameId = selectedItem.GetMetadata(0).AsInt32();
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var variables = await _runService.GetVariablesForStackFrame(frameId);
		await this.InvokeAsync(() =>
		{
			_variablesTree.Clear();
			var root = _variablesTree.CreateItem();
			foreach (var variable in variables)
			{
				AddVariableToTreeItem(root, variable);
			}
		});
	}
	
	private void AddVariableToTreeItem(TreeItem parentItem, Variable variable)
	{
		var variableItem = _variablesTree.CreateItem(parentItem);
		_variableReferenceLookup[variableItem] = variable;
		var icon = variable.PresentationHint?.Kind switch
		{
			VariablePresentationHint.KindValue.Data => _fieldIcon,
			VariablePresentationHint.KindValue.Property => _propertyIcon,
			VariablePresentationHint.KindValue.Class => _staticMembersIcon,
			_ => null
		};
		if (icon is null)
		{
			// unlike sharpdbg and presumably vsdbg, netcoredbg does not set PresentationHint for variables
			if (variable.Name == "Static members") icon = _staticMembersIcon;
			else icon = _fieldIcon;
		}
		variableItem.SetIcon(0, icon);
		variableItem.SetMetadata(0, new Vector2I(0, variable.VariablesReference));
		if (variable.Name == "Static members")
		{
			variableItem.SetTooltipText(0, null);
			variableItem.SetText(0, "Static members");
		}
		else
		{
			variableItem.SetCellMode(0, TreeItem.TreeCellMode.Custom);
			variableItem.SetCustomAsButton(0, true);
			variableItem.SetCustomDrawCallback(0, _debuggerVariableCustomDrawCallable!.Value);
		}
		if (variable.VariablesReference is not 0)
		{
			var placeHolderItem = _variablesTree.CreateItem(variableItem);
			placeHolderItem.SetText(0, "Loading...");
			variableItem.Collapsed = true;
		}
	}


	private async Task OnDebuggerExecutionStopped(ExecutionStopInfo stopInfo)
	{
		var threads = await _runService.GetThreadsAtStopPoint();
		await this.InvokeAsync(() =>
		{
			_threadsTree.Clear();
			var root = _threadsTree.CreateItem();
			foreach (var thread in threads)
			{
				var threadItem = _threadsTree.CreateItem(root);
				threadItem.SetText(0, $"@{thread.Id}: {thread.Name}");
				threadItem.SetMetadata(0, thread.Id);
				if (thread.Id == stopInfo.ThreadId) _threadsTree.SetSelected(threadItem, 0);
			}
		});
	}
}