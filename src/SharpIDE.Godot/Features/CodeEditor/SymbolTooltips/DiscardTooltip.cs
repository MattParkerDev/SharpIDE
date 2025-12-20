using Godot;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetDiscardSymbolInfo(IDiscardSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddText("discard ");
        label.AddType(symbol.Type);
        label.AddText(" ");
        label.AddDiscard(symbol);
        label.Pop();
        label.Pop();
        return label;
    }

    private static void AddDiscard(this RichTextLabel label, IDiscardSymbol _)
    {
        label.PushColor(CachedColors.VariableBlue);
        label.AddText("_");
        label.Pop();
    }
}