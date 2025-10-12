using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SymbolInfoComponents
{
    public static RichTextLabel GetPropertySymbolInfo(IPropertySymbol symbol)
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
        label.AddPropertyTypeName(symbol);
        label.AddPropertyName(symbol);
        label.AddGetSetAccessors(symbol);
        label.AddContainingNamespaceAndClass(symbol);
        label.Newline();
        //label.AddTypeParameterArguments(symbol);
        label.Pop(); // font
        label.AddDocs(symbol);
        
        label.Pop();
        return label;
    }
    
    private static void AddReadonlyModifier(this RichTextLabel label, IPropertySymbol symbol)
    {
        if (symbol.IsReadOnly)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("readonly");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddPropertyTypeName(this RichTextLabel label, IPropertySymbol symbol)
    {
        label.AddType(symbol.Type);
        label.AddText(" ");
    }
    
    private static void AddPropertyName(this RichTextLabel label, IPropertySymbol symbol)
    {
        label.PushColor(CachedColors.White);
        label.AddText(symbol.Name);
        label.Pop();
    }
    
    private static void AddGetSetAccessors(this RichTextLabel label, IPropertySymbol symbol)
    {
        label.AddText(" { ");
        
        if (symbol.GetMethod is not null)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("get");
            label.Pop();
            label.PushColor(CachedColors.White);
            label.AddText(";");
            label.Pop();
            label.AddText(" ");
        }
        if (symbol.SetMethod is {} setMethod)
        {
            if (setMethod.DeclaredAccessibility != symbol.DeclaredAccessibility)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText(setMethod.DeclaredAccessibility.ToString().ToLower());
                label.Pop();
                label.AddText(" ");
            }

            if (setMethod.IsInitOnly)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("init");
                label.Pop();
                label.PushColor(CachedColors.White);
                label.AddText(";");
                label.Pop();
            }
            else
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("set");
                label.Pop();
                label.PushColor(CachedColors.White);
                label.AddText(";");
                label.Pop();
            }
            
            label.AddText(" ");
        }
        label.AddText("}");
    }
}