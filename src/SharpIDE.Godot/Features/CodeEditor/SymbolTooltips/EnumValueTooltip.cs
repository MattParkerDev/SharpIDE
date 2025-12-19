using Godot;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetEnumValueSymbolInfo(IFieldSymbol symbol)
    {
        if (symbol is { ContainingType.TypeKind: not TypeKind.Enum })
        {
            throw new ArgumentException("The containing type of the symbol must be an enum type.", nameof(symbol));
        }

        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddAttributes(symbol);
        label.AddFieldName(symbol);
        label.AddText($" = {symbol.ConstantValue};");
        label.AddContainingNamespaceAndClass(symbol);
        label.Newline();
        label.Pop();
        label.AddDocs(symbol);
        
        label.Pop();
        return label;
    }
}