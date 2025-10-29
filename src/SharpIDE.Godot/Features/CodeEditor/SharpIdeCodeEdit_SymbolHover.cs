using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Timer = Godot.Timer;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private Timer? _symbolHoverTimer = null!;
    
    private void CloseSymbolHoverWindow()
    {
        _symbolHoverTimer?.EmitSignal(Timer.SignalName.Timeout);
    }
    
	// This method is a bit of a disaster - we create an additional invisible Window, so that the tooltip window doesn't disappear while the mouse is over the hovered symbol
    private async void OnSymbolHovered(string symbol, long line, long column)
    {
        if (HasFocus() is false)
            return; // only show if we have focus, every tab is currently listening for this event, maybe find a better way
        var globalMousePosition = GetGlobalMousePosition(); // don't breakpoint before this, else your mouse position will be wrong
        var lineHeight = GetLineHeight();
        GD.Print($"Symbol hovered: {symbol} at line {line}, column {column}");

        var (roslynSymbol, linePositionSpan) = await _roslynAnalysis.LookupSymbol(_currentFile, new LinePosition((int)line, (int)column));
        if (roslynSymbol is null || linePositionSpan is null)
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
        // To debug location, make type a PopupPanel, and uncomment
        //symbolNameHoverWindow.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(1, 0, 0, 0.5f) });

        var startSymbolCharRect = GetRectAtLineColumn(linePositionSpan.Value.Start.Line, linePositionSpan.Value.Start.Character + 1);
        var endSymbolCharRect = GetRectAtLineColumn(linePositionSpan.Value.End.Line, linePositionSpan.Value.End.Character);
        symbolNameHoverWindow.Size = new Vector2I(endSymbolCharRect.End.X - startSymbolCharRect.Position.X, lineHeight);

        var globalPosition = GetGlobalPosition();
        var startSymbolCharGlobalPos = startSymbolCharRect.Position + globalPosition;
        var endSymbolCharGlobalPos = endSymbolCharRect.Position + globalPosition;

        AddChild(symbolNameHoverWindow);
        symbolNameHoverWindow.Position = new Vector2I((int)startSymbolCharGlobalPos.X, (int)endSymbolCharGlobalPos.Y);

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
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", styleBox);

        var symbolInfoNode = roslynSymbol switch
        {
            IMethodSymbol methodSymbol => SymbolInfoComponents.GetMethodSymbolInfo(methodSymbol),
            INamedTypeSymbol namedTypeSymbol => SymbolInfoComponents.GetNamedTypeSymbolInfo(namedTypeSymbol),
            IPropertySymbol propertySymbol => SymbolInfoComponents.GetPropertySymbolInfo(propertySymbol),
            IFieldSymbol fieldSymbol => SymbolInfoComponents.GetFieldSymbolInfo(fieldSymbol),
            IParameterSymbol parameterSymbol => SymbolInfoComponents.GetParameterSymbolInfo(parameterSymbol),
            ILocalSymbol localSymbol => SymbolInfoComponents.GetLocalVariableSymbolInfo(localSymbol),
            _ => SymbolInfoComponents.GetUnknownTooltip(roslynSymbol)
        };
        symbolInfoNode.FitContent = true;
        symbolInfoNode.AutowrapMode = TextServer.AutowrapMode.Off;
        symbolInfoNode.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        panel.AddChild(symbolInfoNode);
        var vboxContainer = new VBoxContainer();
        vboxContainer.AddThemeConstantOverride("separation", 0);
        vboxContainer.AddChild(panel);
        tooltipWindow.AddChild(vboxContainer);
        tooltipWindow.ChildControlsChanged();
        AddChild(tooltipWindow);

        tooltipWindow.Position = new Vector2I((int)globalMousePosition.X, (int)startSymbolCharGlobalPos.Y + lineHeight);
        symbolNameHoverWindow.Popup();
        tooltipWindow.Popup();
    }
}