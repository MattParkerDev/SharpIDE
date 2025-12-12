using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Debug_.Tab.SubTabs;

public partial class ThreadsVariablesSubTab : Control
{
	private PackedScene _threadListItemScene = GD.Load<PackedScene>("res://Features/Debug_/Tab/SubTabs/ThreadListItem.tscn");
	
	private Tree _threadsTree = null!;
	private Tree _stackFramesTree = null!;
	private Tree _variablesTree = null!;
	
	public SharpIdeProjectModel Project { get; set; } = null!;
	// private ThreadModel? _selectedThread = null!; // null when not at a stop point
	
    [Inject] private readonly RunService _runService = null!;

	public override void _Ready()
	{
		_threadsTree = GetNode<Tree>("%ThreadsTree");
		_stackFramesTree = GetNode<Tree>("%StackFramesTree");
		_variablesTree = GetNode<Tree>("%VariablesTree");
		GlobalEvents.Instance.DebuggerExecutionStopped.Subscribe(OnDebuggerExecutionStopped2);
		_threadsTree.ItemSelected += OnThreadSelected;
		_stackFramesTree.ItemSelected += OnStackFrameSelected;
	}
	
	private async void OnThreadSelected()
	{
		var selectedItem = _threadsTree.GetSelected();
		Guard.Against.Null(selectedItem);
		var threadId = selectedItem.GetMetadata(0).AsInt32();
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
		var variables = await _runService.GetVariablesForStackFrame(frameId);
		await this.InvokeAsync(() =>
		{
			_variablesTree.Clear();
			var root = _variablesTree.CreateItem();
			foreach (var variable in variables)
			{
				var variableItem = _variablesTree.CreateItem(root);
				variableItem.SetText(0, $$"""{{variable.Name}} = {{{variable.Type}}} {{variable.Value}}""");
			}
		});
	}


	private async Task OnDebuggerExecutionStopped2(ExecutionStopInfo stopInfo)
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