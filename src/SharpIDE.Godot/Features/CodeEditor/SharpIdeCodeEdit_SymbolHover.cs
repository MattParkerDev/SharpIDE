using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Timer = Godot.Timer;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private Timer? _symbolHoverTimer = null!;

    private RichTextLabel? _symbolInfoRichTextLabel;
    private RichTextLabel? _diagnosticInfoRichTextLabel;

    private void CloseSymbolHoverWindow()
    {
        _symbolHoverTimer?.EmitSignal(Timer.SignalName.Timeout);
    }

	// This method is a bit of a disaster - we create an additional invisible Window, so that the tooltip window doesn't disappear while the mouse is over the hovered symbol
    private async void OnSymbolHovered(string symbol, long line, long column)
    {
        if (Visible is false) return; // This should never return false, as this signal should not be emitted when this SharpIdeCodeEdit is not the selected tab, ie the control is not visible
        var globalMousePosition = GetGlobalMousePosition(); // don't breakpoint before this, else your mouse position will be wrong
        var lineHeight = GetLineHeight();
        GD.Print($"Symbol hovered: {symbol} at line {line}, column {column}");

        var linePosition = new LinePosition((int)line, (int)column);
        var (roslynSymbol, symbolLinePositionSpan) = await _roslynAnalysis.LookupSymbol(_currentFile, linePosition);

        var diagnostics = _fileDiagnostics.AsEnumerable().Concat(_fileAnalyzerDiagnostics).Concat(_projectDiagnosticsForFile);
        var diagnosticsForLinePosition = diagnostics.FirstOrDefault(s => s.Span.Start.Line == linePosition.Line &&
                                                                      s.Span.Start.Character <= linePosition.Character &&
                                                                      s.Span.End.Character >= linePosition.Character);

        if ((roslynSymbol is null || symbolLinePositionSpan is null) && diagnosticsForLinePosition is null)
        {
            return;
        }

        var symbolNameHoverWindow = new Window();
        symbolNameHoverWindow.WrapControls = true;
        symbolNameHoverWindow.Unresizable = true;
        symbolNameHoverWindow.Transparent = true;
        symbolNameHoverWindow.Borderless = true;
        symbolNameHoverWindow.PopupWMHint = true;
        symbolNameHoverWindow.PopupWindow = true;
        symbolNameHoverWindow.MinimizeDisabled = true;
        symbolNameHoverWindow.MaximizeDisabled = true;
        symbolNameHoverWindow.Exclusive = false;
        symbolNameHoverWindow.Transient = true;
        symbolNameHoverWindow.TransientToFocused = true;
        symbolNameHoverWindow.Unfocusable = true;
        symbolNameHoverWindow.WindowInput += (inputEvent) =>
        {
            if (inputEvent is InputEventMouseButton mouseButtonEvent && mouseButtonEvent.Pressed)
            {
                CloseSymbolHoverWindow();
            }
        };
        // To debug location, make type a PopupPanel, and uncomment
        //symbolNameHoverWindow.AddThemeStyleboxOverride(ThemeStringNames.Panel, new StyleBoxFlat { BgColor = new Color(1, 0, 0, 0.5f) });

        var globalPosition = GetGlobalPosition();
        Vector2 startSymbolCharGlobalPos;
        if (symbolLinePositionSpan is not null)
        {
            var startSymbolCharRect = GetRectAtLineColumn(symbolLinePositionSpan.Value.Start.Line, symbolLinePositionSpan.Value.Start.Character + 1);
            var endSymbolCharRect = GetRectAtLineColumn(symbolLinePositionSpan.Value.End.Line, symbolLinePositionSpan.Value.End.Character);
            symbolNameHoverWindow.Size = new Vector2I(endSymbolCharRect.End.X - startSymbolCharRect.Position.X, lineHeight);
            startSymbolCharGlobalPos = startSymbolCharRect.Position + globalPosition;
            var endSymbolCharGlobalPos = endSymbolCharRect.Position + globalPosition;
            AddChild(symbolNameHoverWindow);
            symbolNameHoverWindow.Position = new Vector2I((int)startSymbolCharGlobalPos.X, (int)endSymbolCharGlobalPos.Y);
        }
        else
        {
            var hoveredCharRect = GetRectAtLineColumn((int)line, (int)column);
            symbolNameHoverWindow.Size = new Vector2I(lineHeight, lineHeight);
            startSymbolCharGlobalPos = hoveredCharRect.Position + globalPosition;
            AddChild(symbolNameHoverWindow);
            symbolNameHoverWindow.Position = new Vector2I((int)startSymbolCharGlobalPos.X, (int)startSymbolCharGlobalPos.Y);
        }

        var tooltipWindow = new Window();
        tooltipWindow.WrapControls = true;
        tooltipWindow.Unresizable = true;
        tooltipWindow.Transparent = true;
        tooltipWindow.Borderless = true;
        tooltipWindow.PopupWMHint = true;
        tooltipWindow.PopupWindow = true;
        tooltipWindow.MinimizeDisabled = true;
        tooltipWindow.MaximizeDisabled = true;
        tooltipWindow.Exclusive = false;
        tooltipWindow.Transient = true;
        tooltipWindow.TransientToFocused = true;
        tooltipWindow.Unfocusable = true;

        var timer = new Timer { WaitTime = 0.05f, OneShot = true, Autostart = false };
        tooltipWindow.AddChild(timer);
        timer.Timeout += () =>
        {
            tooltipWindow.QueueFree();
            symbolNameHoverWindow.QueueFree();
            tooltipWindow.MouseExited -= StartTimer;
            tooltipWindow.MouseEntered -= StopTimer;
            symbolNameHoverWindow.MouseExited -= StartTimer;
            symbolNameHoverWindow.MouseEntered -= StopTimer;
            _symbolHoverTimer = null;
            _symbolInfoRichTextLabel = null;
			_diagnosticInfoRichTextLabel = null;
        };
        _symbolHoverTimer = timer;

        void StartTimer() => timer.Start();
        void StopTimer() => timer.Stop();
        tooltipWindow.MouseExited += StartTimer;
        tooltipWindow.MouseEntered += StopTimer;
        symbolNameHoverWindow.MouseExited += StartTimer;
        symbolNameHoverWindow.MouseEntered += StopTimer;

        var styleBox = new StyleBoxFlat
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
            ExpandMarginTop = -2, // negative margin seems to fix shadow being cut off?
            ExpandMarginBottom = -2,
            ExpandMarginLeft = -2,
            ExpandMarginRight = -2,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            ContentMarginLeft = 12,
            ContentMarginRight = 12
        };

		PanelContainer? diagnosticPanel = null;
        if (diagnosticsForLinePosition is not null)
        {
            var diagnosticNode = SymbolInfoComponents.GetDiagnostic(diagnosticsForLinePosition);
            diagnosticNode.FitContent = true;
            diagnosticNode.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            diagnosticNode.SetAnchorsPreset(LayoutPreset.FullRect);
            diagnosticNode.SelectionEnabled = true;
            diagnosticPanel = new PanelContainer();
            diagnosticPanel.AddThemeStyleboxOverride(ThemeStringNames.Panel, styleBox);
            diagnosticPanel.AddChild(diagnosticNode);
            _diagnosticInfoRichTextLabel = diagnosticNode;
        }

        var symbolInfoNode = roslynSymbol switch
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
            not null => SymbolInfoComponents.GetUnknownTooltip(roslynSymbol),
            _ => null
        };
        PanelContainer? symbolInfoPanel = null;
        if (symbolInfoNode is not null)
        {
            symbolInfoNode.FitContent = true;
            symbolInfoNode.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            symbolInfoNode.SetAnchorsPreset(LayoutPreset.FullRect);
            symbolInfoNode.SelectionEnabled = true;
            symbolInfoPanel = new PanelContainer();
            symbolInfoPanel.AddThemeStyleboxOverride(ThemeStringNames.Panel, styleBox);
            symbolInfoPanel.AddChild(symbolInfoNode);
            _symbolInfoRichTextLabel = symbolInfoNode;
        }

        var vboxContainer = new VBoxContainer();
        vboxContainer.AddThemeConstantOverride(ThemeStringNames.Separation, 0);
        if (diagnosticPanel is not null) vboxContainer.AddChild(diagnosticPanel);
        if (symbolInfoPanel is not null) vboxContainer.AddChild(symbolInfoPanel);

        var rightEdgePosition = GetViewport().GetVisibleRect().Size.X;
		var maxWidth = rightEdgePosition - globalMousePosition.X - 20;

        vboxContainer.CustomMaximumSize = new Vector2(maxWidth, -1);
        tooltipWindow.AddChild(vboxContainer);
        tooltipWindow.ChildControlsChanged();
        AddChild(tooltipWindow);

        tooltipWindow.Position = new Vector2I((int)globalMousePosition.X, (int)startSymbolCharGlobalPos.Y + lineHeight);
        symbolNameHoverWindow.Popup();
        tooltipWindow.Popup();
        symbolNameHoverWindow.UpdateMouseCursorState();
    }
}
