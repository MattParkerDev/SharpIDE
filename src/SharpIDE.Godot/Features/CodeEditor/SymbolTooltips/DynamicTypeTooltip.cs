using Godot;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetDynamicTypeSymbolInfo(IDynamicTypeSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText(symbol.ToDisplayString());
        label.Pop();
        label.Pop();
        label.Pop();
        return label;
    }
}