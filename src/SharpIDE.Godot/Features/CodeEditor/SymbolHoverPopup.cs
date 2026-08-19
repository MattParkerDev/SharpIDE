using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using Timer = Godot.Timer;

namespace SharpIDE.Godot.Features.CodeEditor;

public sealed class SymbolHoverPopup
{
	private readonly CodeEdit _codeEdit;
	private Timer? _closeTimer;
	private RichTextLabel? _symbolInfoLabel;
	private RichTextLabel? _diagnosticInfoLabel;

	public SymbolHoverPopup(CodeEdit codeEdit) => _codeEdit = codeEdit;

	public void Show(ISymbol? symbol, LinePositionSpan? symbolSpan, SharpIdeDiagnostic? diagnostic, LinePosition hoveredPosition)
	{
		Close();
		if (symbol is null && diagnostic is null)
		{
			return;
		}

		var globalMousePosition = _codeEdit.GetGlobalMousePosition();
		var lineHeight = _codeEdit.GetLineHeight();
		var globalPosition = _codeEdit.GetGlobalPosition();
		var hoverBridgeWindow = CreateWindow();
		Vector2 anchorGlobalPosition;
		if (symbolSpan is not null)
		{
			var startRect = _codeEdit.GetRectAtLineColumn(symbolSpan.Value.Start.Line, symbolSpan.Value.Start.Character + 1);
			var endRect = _codeEdit.GetRectAtLineColumn(symbolSpan.Value.End.Line, symbolSpan.Value.End.Character);
			hoverBridgeWindow.Size = new Vector2I((int)(endRect.End.X - startRect.Position.X), lineHeight);
			anchorGlobalPosition = startRect.Position + globalPosition;
			_codeEdit.AddChild(hoverBridgeWindow);
			hoverBridgeWindow.Position = new Vector2I((int)anchorGlobalPosition.X, (int)(endRect.Position.Y + globalPosition.Y));
		}
		else
		{
			var hoveredRect = _codeEdit.GetRectAtLineColumn(hoveredPosition.Line, hoveredPosition.Character);
			hoverBridgeWindow.Size = new Vector2I(lineHeight, lineHeight);
			anchorGlobalPosition = hoveredRect.Position + globalPosition;
			_codeEdit.AddChild(hoverBridgeWindow);
			hoverBridgeWindow.Position = new Vector2I((int)anchorGlobalPosition.X, (int)anchorGlobalPosition.Y);
		}

		var tooltipWindow = CreateWindow();
		var timer = new Timer { WaitTime = 0.05f, OneShot = true };
		tooltipWindow.AddChild(timer);
		timer.Timeout += () =>
		{
			tooltipWindow.QueueFree();
			hoverBridgeWindow.QueueFree();
			_closeTimer = null;
			_symbolInfoLabel = null;
			_diagnosticInfoLabel = null;
		};
		_closeTimer = timer;

		void StartTimer() => timer.Start();
		void StopTimer() => timer.Stop();
		tooltipWindow.MouseExited += StartTimer;
		tooltipWindow.MouseEntered += StopTimer;
		hoverBridgeWindow.MouseExited += StartTimer;
		hoverBridgeWindow.MouseEntered += StopTimer;
		hoverBridgeWindow.WindowInput += inputEvent =>
		{
			if (inputEvent is InputEventMouseButton { Pressed: true })
			{
				Close();
			}
		};

		var content = new VBoxContainer();
		content.AddThemeConstantOverride(ThemeStringNames.Separation, 0);
		if (diagnostic is not null)
		{
			_diagnosticInfoLabel = SymbolInfoComponents.GetDiagnostic(diagnostic);
			content.AddChild(CreatePanel(_diagnosticInfoLabel));
		}

		var symbolInfo = CreateSymbolInfo(symbol);
		if (symbolInfo is not null)
		{
			_symbolInfoLabel = symbolInfo;
			content.AddChild(CreatePanel(symbolInfo));
		}

		var rightEdge = _codeEdit.GetViewport().GetVisibleRect().Size.X;
		content.CustomMaximumSize = new Vector2(Math.Max(0, rightEdge - globalMousePosition.X - 20), -1);
		tooltipWindow.AddChild(content);
		tooltipWindow.ChildControlsChanged();
		_codeEdit.AddChild(tooltipWindow);
		tooltipWindow.Position = new Vector2I((int)globalMousePosition.X, (int)anchorGlobalPosition.Y + lineHeight);
		hoverBridgeWindow.Popup();
		tooltipWindow.Popup();
		hoverBridgeWindow.UpdateMouseCursorState();
	}

	public void Close() => _closeTimer?.EmitSignal(Timer.SignalName.Timeout);

	public bool TryCopySelectedText()
	{
		var selectedText = GetSelectedText(_symbolInfoLabel) ?? GetSelectedText(_diagnosticInfoLabel);
		if (string.IsNullOrEmpty(selectedText))
		{
			return false;
		}

		DisplayServer.ClipboardSet(selectedText);
		return true;
	}

	private static Window CreateWindow() => new()
	{
		WrapControls = true,
		Unresizable = true,
		Transparent = true,
		Borderless = true,
		PopupWMHint = true,
		PopupWindow = true,
		MinimizeDisabled = true,
		MaximizeDisabled = true,
		Exclusive = false,
		Transient = true,
		TransientToFocused = true,
		Unfocusable = true,
	};

	private static PanelContainer CreatePanel(RichTextLabel label)
	{
		label.FitContent = true;
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		label.SelectionEnabled = true;
		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride(ThemeStringNames.Panel, new StyleBoxFlat
		{
			BgColor = new Color("2b2d30"),
			BorderColor = new Color("3e4045"),
			BorderWidthTop = 1,
			BorderWidthBottom = 1,
			BorderWidthLeft = 1,
			BorderWidthRight = 1,
			CornerRadiusBottomLeft = 4,
			CornerRadiusBottomRight = 4,
			CornerRadiusTopLeft = 4,
			CornerRadiusTopRight = 4,
			ShadowSize = 2,
			ShadowColor = new Color(0, 0, 0, 0.5f),
			ExpandMarginTop = -2,
			ExpandMarginBottom = -2,
			ExpandMarginLeft = -2,
			ExpandMarginRight = -2,
			ContentMarginTop = 10,
			ContentMarginBottom = 10,
			ContentMarginLeft = 12,
			ContentMarginRight = 12,
		});
		panel.AddChild(label);
		return panel;
	}

	private static RichTextLabel? CreateSymbolInfo(ISymbol? symbol) => symbol switch
	{
		IMethodSymbol methodSymbol => SymbolInfoComponents.GetMethodSymbolInfo(methodSymbol),
		INamedTypeSymbol namedTypeSymbol => SymbolInfoComponents.GetNamedTypeSymbolInfo(namedTypeSymbol),
		IPropertySymbol propertySymbol => SymbolInfoComponents.GetPropertySymbolInfo(propertySymbol),
		IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } fieldSymbol => SymbolInfoComponents.GetEnumValueSymbolInfo(fieldSymbol),
		IFieldSymbol fieldSymbol => SymbolInfoComponents.GetFieldSymbolInfo(fieldSymbol),
		IParameterSymbol parameterSymbol => SymbolInfoComponents.GetParameterSymbolInfo(parameterSymbol),
		ILocalSymbol localSymbol => SymbolInfoComponents.GetLocalVariableSymbolInfo(localSymbol),
		INamespaceSymbol namespaceSymbol => SymbolInfoComponents.GetNamespaceSymbolInfo(namespaceSymbol),
		ITypeParameterSymbol typeParameterSymbol => SymbolInfoComponents.GetTypeParameterSymbolInfo(typeParameterSymbol),
		IDynamicTypeSymbol dynamicTypeSymbol => SymbolInfoComponents.GetDynamicTypeSymbolInfo(dynamicTypeSymbol),
		IEventSymbol eventSymbol => SymbolInfoComponents.GetEventSymbolInfo(eventSymbol),
		IDiscardSymbol discardSymbol => SymbolInfoComponents.GetDiscardSymbolInfo(discardSymbol),
		ILabelSymbol labelSymbol => SymbolInfoComponents.GetLabelSymbolInfo(labelSymbol),
		not null => SymbolInfoComponents.GetUnknownTooltip(symbol),
		_ => null,
	};

	private static string? GetSelectedText(RichTextLabel? label)
		=> label is not null && label.GetSelectionFrom() is not -1 ? label.GetSelectedText() : null;
}
