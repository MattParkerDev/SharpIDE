using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetParameterSymbolInfo(IParameterSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddAttributes(symbol);
        label.AddText("parameter ");
        label.AddAccessibilityModifier(symbol);
        label.AddStaticModifier(symbol);
        label.AddVirtualModifier(symbol);
        label.AddAbstractModifier(symbol);
        label.AddOverrideModifier(symbol);
        label.AddParameterTypeName(symbol);
        label.AddParameterName(symbol);
        label.AddContainingNamespaceAndClass(symbol);
        label.Newline();
        //label.AddTypeParameterArguments(symbol);
        label.Pop(); // font
        label.AddDocs(symbol);
        
        label.Pop();
        return label;
    }
    
    private static void AddParameterTypeName(this RichTextLabel label, IParameterSymbol symbol)
    {
        label.AddType(symbol.Type);
        label.AddText(" ");
    }
    
    private static void AddParameterName(this RichTextLabel label, IParameterSymbol symbol)
    {
        label.PushColor(CachedColors.VariableBlue);
        label.AddText(symbol.Name);
        label.Pop();
    }
}