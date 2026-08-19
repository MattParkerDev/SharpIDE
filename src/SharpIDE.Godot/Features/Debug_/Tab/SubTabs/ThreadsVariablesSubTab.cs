using Ardalis.GuardClauses;
using Godot;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.Debug_.Tab.SubTabs;

public partial class ThreadsVariablesSubTab : Control
{
	private PackedScene _threadListItemScene = GD.Load<PackedScene>("res://Features/Debug_/Tab/SubTabs/ThreadListItem.tscn");

	private readonly Texture2D _fieldIcon = ResourceLoader.Load<Texture2D>("uid://c4y7d5m4upfju");
	private readonly Texture2D _propertyIcon = ResourceLoader.Load<Texture2D>("uid://y5pwrwwrjqmc");
	private readonly Texture2D _staticMembersIcon = ResourceLoader.Load<Texture2D>("uid://dudntp20myuxb");
	private readonly Texture2D _arrayElementIcon = ResourceLoader.Load<Texture2D>("uid://cppysddplcd6d");
	private readonly Texture2D _exceptionIcon = ResourceLoader.Load<Texture2D>("uid://c3upo3lxmgtls");

	private Tree _threadsTree = null!;
	private Tree _stackFramesTree = null!;
	private Tree _variablesTree = null!;
	private DebuggerEvalExpressionCodeEdit _evaluateExpressionCodeEdit = null!;
	private Dictionary<int, StackFrameModel> _stackFramesById = [];
	private TreeItem? _evaluationResultItem;
	private int _evaluationVersion;

	public SharpIdeProjectModel Project { get; set; } = null!;
	// private ThreadModel? _selectedThread = null!; // null when not at a stop point

    [Inject] private readonly RunService _runService = null!;
    [Inject] private readonly SharpIdeSolutionAccessor _solutionAccessor = null!;

    private Callable? _debuggerVariableCustomDrawCallable;
    private readonly Dictionary<TreeItem, Variable> _variableReferenceLookup = []; // primarily used for DebuggerVariableCustomDraw

	public override void _Ready()
	{
		_threadsTree = GetNode<Tree>("%ThreadsTree");
		_stackFramesTree = GetNode<Tree>("%StackFramesTree");
		_variablesTree = GetNode<Tree>("%VariablesTree");
		_evaluateExpressionCodeEdit = GetNode<DebuggerEvalExpressionCodeEdit>("%DebuggerEvalExpressionCodeEdit");
		_debuggerVariableCustomDrawCallable = new Callable(this, MethodName.DebuggerVariableCustomDraw);
		GlobalEvents.Instance.DebuggerExecutionStopped.Subscribe(OnDebuggerExecutionStopped);
		GlobalEvents.Instance.DebuggerExecutionContinued.Subscribe(ClearAllTrees);
		_threadsTree.ItemSelected += OnThreadSelected;
		_stackFramesTree.ItemSelected += OnStackFrameSelected;
		_variablesTree.ItemCollapsed += OnVariablesItemExpandedOrCollapsed;
		_evaluateExpressionCodeEdit.ExpressionSubmitted += OnExpressionSubmitted;
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
		_evaluateExpressionCodeEdit.ExpressionSubmitted -= OnExpressionSubmitted;
	}

	// TODO: this should check which project was continued, like OnDebuggerExecutionStopped
	private async Task ClearAllTrees()
	{
		await this.InvokeAsync(() =>
		{
			_evaluationVersion++;
			_threadsTree.Clear();
			_stackFramesTree.Clear();
			_variablesTree.Clear();
			_evaluationResultItem = null;
			_variableReferenceLookup.Clear();
			_stackFramesById.Clear();
			_evaluateExpressionCodeEdit.ClearContext();
		});
	}

	private async void OnThreadSelected()
	{
		var selectedItem = _threadsTree.GetSelected();
		Guard.Against.Null(selectedItem);
		var threadId = selectedItem.GetMetadata(0).AsInt32();
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var stackFrames = await _runService.GetStackFrames(threadId);
		_stackFramesById = stackFrames.ToDictionary(frame => frame.Id);
		await this.InvokeAsync(() =>
		{
			_evaluationVersion++;
			_variablesTree.Clear(); // If we select a thread that does not have stack frames, the variables would not be cleared otherwise
			_evaluationResultItem = null;
			_variableReferenceLookup.Clear();
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
		if (_stackFramesById.TryGetValue(frameId, out var stackFrame) is false)
		{
			return;
		}
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var variablesTask = _runService.GetVariablesForStackFrame(frameId);
		var expressionContextTask = SetExpressionContextAsync(stackFrame);
		await Task.WhenAll(variablesTask, expressionContextTask);
		var variables = await variablesTask;
		await this.InvokeAsync(() =>
		{
			_evaluationVersion++;
			_variablesTree.Clear();
			_evaluationResultItem = null;
			_variableReferenceLookup.Clear();
			var root = _variablesTree.CreateItem();
			foreach (var variable in variables)
			{
				AddVariableToTreeItem(root, variable);
			}
		});
	}

	private TreeItem AddVariableToTreeItem(TreeItem parentItem, Variable variable, int index = -1)
	{
		var variableItem = _variablesTree.CreateItem(parentItem, index);
		_variableReferenceLookup[variableItem] = variable;

		variableItem.SetMetadata(0, new Vector2I(0, variable.VariablesReference));
		if (variable.Name is "Static members" or "Raw View")
		{
			variableItem.SetTooltipText(0, null);
			variableItem.SetIcon(0, _staticMembersIcon);
			variableItem.SetText(0, variable.Name);
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

		return variableItem;
	}

	private void OnExpressionSubmitted(string expression)
	{
		var selectedFrameItem = _stackFramesTree.GetSelected();
		if (selectedFrameItem is null)
		{
			return;
		}

		var frameId = selectedFrameItem.GetMetadata(0).AsInt32();
		var evaluationVersion = ++_evaluationVersion;
		_ = Task.GodotRun(async () =>
		{
			Variable resultVariable;
			try
			{
				var response = await _runService.EvaluateExpression(frameId, expression);
				resultVariable = new Variable("$result", response.Result, response.VariablesReference)
				{
					Type = response.Type,
					PresentationHint = response.PresentationHint,
					EvaluateName = expression,
					NamedVariables = response.NamedVariables,
					IndexedVariables = response.IndexedVariables,
					MemoryReference = response.MemoryReference,
				};
			}
			catch (ProtocolException exception)
			{
				resultVariable = new Variable("$result", exception.Message, 0)
				{
					PresentationHint = new VariablePresentationHint
					{
						Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation,
					},
				};
			}

			await this.InvokeAsync(() =>
			{
				var currentFrameItem = _stackFramesTree.GetSelected();
				if (evaluationVersion != _evaluationVersion || currentFrameItem is null || currentFrameItem.GetMetadata(0).AsInt32() != frameId)
				{
					return;
				}

				var root = _variablesTree.GetRoot();
				if (root is null)
				{
					return;
				}

				if (_evaluationResultItem is not null)
				{
					_variableReferenceLookup.Remove(_evaluationResultItem);
					_evaluationResultItem.Free();
				}

				_evaluationResultItem = AddVariableToTreeItem(root, resultVariable, 0);
			});
		});
	}


	private async Task OnDebuggerExecutionStopped(ExecutionStopInfo stopInfo)
	{
		if (stopInfo.Project != Project) return;

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

	private async Task SetExpressionContextAsync(StackFrameModel stackFrame)
	{
		if (stackFrame is { IsExternalCode: true } or { Source: null } or { Line: null } or { Column: null })
		{
			await this.InvokeAsync(_evaluateExpressionCodeEdit.ClearContext);
			return;
		}

		var solution = _solutionAccessor.SolutionModel;
		var file = solution.AllFiles.GetValueOrDefault(stackFrame.Source);
		if (file is null || file.IsCsharpFile is false)
		{
			await this.InvokeAsync(_evaluateExpressionCodeEdit.ClearContext);
			return;
		}

		var sourceContextPosition = new LinePosition(
			Math.Max(0, stackFrame.Line.Value - 1),
			Math.Max(0, stackFrame.Column.Value - 1));
		await _evaluateExpressionCodeEdit.SetContextAsync(file, sourceContextPosition);
	}
}
