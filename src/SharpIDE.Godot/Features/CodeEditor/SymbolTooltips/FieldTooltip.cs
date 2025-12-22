using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetFieldSymbolInfo(IFieldSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddAttributes(symbol);
        label.AddAccessibilityModifier(symbol);
        label.AddStaticModifier(symbol);
        label.AddReadonlyModifier(symbol);
        label.AddVirtualModifier(symbol);
        label.AddAbstractModifier(symbol);
        label.AddOverrideModifier(symbol);
        label.AddRequiredModifier(symbol);
        label.AddFieldTypeName(symbol);
        label.AddFieldName(symbol);
        label.AddText(";");
        label.AddContainingNamespaceAndClass(symbol);
        label.Newline();
        //label.AddTypeParameterArguments(symbol);
        label.Pop(); // font
        label.AddDocs(symbol);
        
        label.Pop();
        return label;
    }
    
    private static void AddStaticModifier(this RichTextLabel label, ISymbol symbol)
    {
        if (symbol.IsStatic)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("static");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddReadonlyModifier(this RichTextLabel label, IFieldSymbol fieldSymbol)
    {
        if (fieldSymbol.IsReadOnly)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("readonly");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddRequiredModifier(this RichTextLabel label, IFieldSymbol symbol)
    {
        if (symbol.IsRequired)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("required");
            label.Pop();
            label.AddText(" ");
        }
    }

    private static void AddFieldTypeName(this RichTextLabel label, IFieldSymbol fieldSymbol)
    {
        label.AddType(fieldSymbol.Type);
        label.AddText(" ");
    }
    
    private static void AddFieldName(this RichTextLabel label, IFieldSymbol fieldSymbol)
    {
        label.PushColor(CachedColors.White);
        label.AddText(fieldSymbol.Name);
        label.Pop();
    }
}