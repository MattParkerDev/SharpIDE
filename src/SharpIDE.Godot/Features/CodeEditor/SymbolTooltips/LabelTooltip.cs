using Godot;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetLabelSymbolInfo(ILabelSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddText("label ");
        label.AddLabelName(symbol);
        label.Pop();
        label.Pop();
        return label;
    }

    private static void AddLabelName(this RichTextLabel label, ILabelSymbol symbol)
    {
        label.PushColor(CachedColors.White);
        label.AddText(symbol.Name);
        label.Pop();
    }
}