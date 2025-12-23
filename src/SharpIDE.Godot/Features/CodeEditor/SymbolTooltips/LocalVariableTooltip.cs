using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetLocalVariableSymbolInfo(ILocalSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddAttributes(symbol);
        label.AddText("local variable ");
        label.AddAccessibilityModifier(symbol);
        label.AddStaticModifier(symbol);
        label.AddVirtualModifier(symbol);
        label.AddAbstractModifier(symbol);
        label.AddOverrideModifier(symbol);
        label.AddLocalVariableTypeName(symbol);
        label.AddLocalVariableName(symbol);
        label.Newline();
        label.Pop(); // font
        label.AddDocs(symbol);
        
        label.Pop();
        return label;
    }
    
    private static void AddLocalVariableTypeName(this RichTextLabel label, ILocalSymbol symbol)
    {
        label.AddType(symbol.Type);
        label.AddText(" ");
    }
    
    private static void AddLocalVariableName(this RichTextLabel label, ILocalSymbol symbol)
    {
        label.PushColor(CachedColors.VariableBlue);
        label.AddText(symbol.Name);
        label.Pop();
    }
}