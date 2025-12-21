using Godot;

namespace SharpIDE.Godot.Features.Layout;

public partial class IdeDockOverlay : Control
{
	private readonly List<Control> _dockTargets = [];

	private IReadOnlyCollection<DockSymbol> _dockSymbols = null!;

	[Export]
	public float DockPreviewSize { get; set; } = 0.3f;

	[Export]
	public Color DockPreviewColor { get; set; } = CachedColors.Gray;

	[Export]
	public Control GlobalDockSymbols { get; set; } = null!;

	[Export]
	public Control LocalDockSymbols { get; set; } = null!;

	public Control? HoveredTarget { get; private set; }

	public DockScope CurrentDockScope { get; private set; }

	public DockPosition CurrentDockPosition { get; private set; }

	public override void _Ready()
	{
		_dockSymbols = GlobalDockSymbols.GetChildren()
										.OfType<DockSymbol>()
										.Concat(LocalDockSymbols.GetChildren().OfType<DockSymbol>())
										.ToArray();
	}

	/// <inheritdoc />
	public override void _Process(double delta)
	{
		if (Visible)
		{
			UpdateDockPreview(GetGlobalMousePosition());
		}
	}

	/// <inheritdoc />
	public override void _Draw()
	{
		DrawDockPreview();
	}

	public void RegisterDockTarget(Control node)
	{
		_dockTargets.Add(node);
	}

	public void ClearDockTargets()
	{
		_dockTargets.Clear();
	}

	private void DrawDockPreview()
	{
		switch (CurrentDockScope)
		{
			case DockScope.Global:
				DrawTargetDockPreview(this);
				break;
			case DockScope.Local when HoveredTarget is not null:
				DrawTargetDockPreview(HoveredTarget);
				break;
		}
	}

	private void DrawTargetDockPreview(Control target)
	{
		var rect = target.GetGlobalRect();
		var localRect = new Rect2(
			rect.Position - GlobalPosition,
			rect.Size);

		var highlight = CurrentDockPosition switch
		{
			DockPosition.Left => new Rect2(
				localRect.Position,
				new Vector2(localRect.Size.X * DockPreviewSize, localRect.Size.Y)),
			DockPosition.Right => new Rect2(
				localRect.Position + new Vector2(localRect.Size.X * (1.0f - DockPreviewSize), 0),
				new Vector2(localRect.Size.X * DockPreviewSize, localRect.Size.Y)),
			DockPosition.Top => new Rect2(
				localRect.Position,
				new Vector2(localRect.Size.X, localRect.Size.Y * DockPreviewSize)),
			DockPosition.Bottom => new Rect2(
				localRect.Position + new Vector2(0, localRect.Size.Y * (1.0f - DockPreviewSize)),
				new Vector2(localRect.Size.X, localRect.Size.Y * DockPreviewSize)),

			_ => new Rect2(Vector2.Zero, Vector2.Zero)
		};

		DrawRect(highlight, DockPreviewColor, filled: true);
	}

	private void UpdateDockPreview(Vector2 mousePosition)
	{
		(CurrentDockScope, CurrentDockPosition) = GetDockScopeAndPosition(mousePosition);

		HoveredTarget = FindDockTarget(mousePosition);

		if (HoveredTarget is null)
		{
			LocalDockSymbols.Visible = false;
			QueueRedraw();
			return;
		}

		var targetCenterPosition = HoveredTarget.GlobalPosition
								 - GlobalPosition
								 + HoveredTarget.Size / 2.0f
								 - LocalDockSymbols.Size / 2.0f;

		LocalDockSymbols.Position = targetCenterPosition;
		LocalDockSymbols.Visible = true;

		QueueRedraw();
	}

	private Control? FindDockTarget(Vector2 mousePosition)
	{
		foreach (var target in _dockTargets)
		{
			if (!target.Visible)
			{
				continue;
			}

			if (target.GetGlobalRect().HasPoint(mousePosition))
			{
				return target;
			}
		}

		return null;
	}

	private (DockScope Scope, DockPosition Position) GetDockScopeAndPosition(Vector2 mousePosition)
	{
		var hoveredSymbol = _dockSymbols.FirstOrDefault(symbol => symbol.GetGlobalRect().HasPoint(mousePosition));

		return hoveredSymbol is not null
				   ? (hoveredSymbol.DockScope, hoveredSymbol.DockPosition)
				   : (DockScope.None, DockPosition.None);
	}
}
