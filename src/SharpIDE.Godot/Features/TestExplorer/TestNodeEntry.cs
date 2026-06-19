using Godot;
using SharpIDE.Application.Features.Testing;
using SharpIDE.Application.Features.Testing.Client;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Godot.Features.TestExplorer;

public partial class TestNodeEntry : MarginContainer
{
	private Label _testNameLabel = null!;
	private Label _testNodeStatusLabel = null!;
	private const int _indentWidth = 14;
	private bool _isHovered;

	public TestNode? TestNode { get; set; }
	public TestNodeHierarchy? TestNodeHierarchy { get; set; }
	public event Action<TestNodeHierarchy>? GroupClicked;
	private static readonly Color SuccessTextColour = new Color("499c54");
	private static readonly Color RunningTextColour = new Color("a77fd2");
	private static readonly Color PendingTextColour = new Color("2aa9e7");
	private static readonly Color FailedTextColour = new Color("c65344");
	private static readonly Color CancelledTextColour = new Color("e4a631");
	private static readonly Color SkippedTextColour = new Color("c0c0c0");
	private static readonly StringName MarginLeftThemeConstant = "margin_left";
	private static readonly Color HoverBackgroundColour = new(1, 1, 1, 0.06f);

	public override void _Ready()
	{
		_testNameLabel = GetNode<Label>("%TestNameLabel");
		_testNodeStatusLabel = GetNode<Label>("%TestNodeStatusLabel");
		MouseFilter = MouseFilterEnum.Stop;
		_testNameLabel.MouseFilter = MouseFilterEnum.Ignore;
		_testNodeStatusLabel.MouseFilter = MouseFilterEnum.Ignore;
		MouseEntered += OnMouseEntered;
		MouseExited += OnMouseExited;
		_testNameLabel.Text = string.Empty;
		_testNodeStatusLabel.Text = string.Empty;
		SetValues();
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (TestNodeHierarchy?.IsGroup is not true)
		{
			return;
		}

		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
		{
			GroupClicked?.Invoke(TestNodeHierarchy);
			AcceptEvent();
		}
	}

	public override void _Draw()
	{
		if (_isHovered)
		{
			DrawRect(new Rect2(Vector2.Zero, Size), HoverBackgroundColour);
		}
	}

	public void SetValues()
	{
		var node = TestNodeHierarchy?.TestNode ?? TestNode;
		var indentLevel = TestNodeHierarchy?.IndentLevel ?? 0;
		AddThemeConstantOverride(MarginLeftThemeConstant, indentLevel * _indentWidth);
		MouseDefaultCursorShape = TestNodeHierarchy?.IsGroup is true ? CursorShape.PointingHand : CursorShape.Arrow;

		if (TestNodeHierarchy is not null)
		{
			_testNameLabel.Text = TestNodeHierarchy.DisplayName;
			_testNodeStatusLabel.Text = TestNodeHierarchy.IsGroup ? string.Empty : node?.ExecutionState ?? string.Empty;
		}
		else if (node is not null)
		{
			_testNameLabel.Text = node.DisplayName;
			_testNodeStatusLabel.Text = node.ExecutionState;
		}
		else
		{
			_testNameLabel.Text = string.Empty;
			_testNodeStatusLabel.Text = string.Empty;
		}

		_testNodeStatusLabel.AddThemeColorOverride(ThemeStringNames.FontColor, GetTextColour(node?.ExecutionState));
	}

	private void OnMouseEntered()
	{
		_isHovered = true;
		QueueRedraw();
	}

	private void OnMouseExited()
	{
		_isHovered = false;
		QueueRedraw();
	}

	private static Color GetTextColour(string? executionState)
	{
		return executionState switch
		{
			ExecutionStates.Passed => SuccessTextColour,
			ExecutionStates.InProgress => RunningTextColour,
			ExecutionStates.Discovered => PendingTextColour,
			ExecutionStates.Failed => FailedTextColour,
			ExecutionStates.Cancelled => CancelledTextColour,
			ExecutionStates.Skipped => SkippedTextColour,
			_ => Colors.White,
		};
	}
}
