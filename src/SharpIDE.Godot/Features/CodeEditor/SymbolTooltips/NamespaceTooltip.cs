using Godot;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetNamespaceSymbolInfo(INamespaceSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushFont(MonospaceFont);
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText("namespace");
        label.Pop();
        label.AddText(" ");
        label.AddNamespace(symbol);
        label.Pop();
        return label;
    }
}