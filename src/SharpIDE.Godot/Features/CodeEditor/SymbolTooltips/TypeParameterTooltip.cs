using Godot;

using ICSharpCode.Decompiler.TypeSystem;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetTypeParameterSymbolInfo(ITypeParameterSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.PushColor(CachedColors.ClassGreen);
        label.AddText(symbol.Name);
        label.Pop();
        label.AddText(" in ");

        if (symbol.DeclaringMethod is { } declaringMethod)
        {
            label.AddDeclaringMethod(declaringMethod);
        }
        else
        {
            label.AddType(symbol.ContainingType);
            label.AddTypeParameterConstraints(symbol.ContainingType.TypeParameters);
        }
        
        label.Pop();
        return label;

    }

    private static void AddDeclaringMethod(this RichTextLabel label, IMethodSymbol symbol)
    {
        label.PushColor(CachedColors.ClassGreen);
        label.AddText(symbol.ContainingType.Name);
        label.Pop();
        label.AddText(".");
        label.AddMethodName(symbol);
        label.AddTypeParameters(symbol);
        label.AddTypeParameterConstraints(symbol.TypeParameters);
    }
}