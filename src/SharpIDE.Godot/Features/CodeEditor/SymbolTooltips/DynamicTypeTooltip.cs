using Godot;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetDynamicTypeSymbolInfo(IDynamicTypeSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(TextEditorDotnetColoursDark.White);
        label.PushFont(MonospaceFont);
        label.PushColor(TextEditorDotnetColoursDark.KeywordBlue);
        label.AddText(symbol.ToDisplayString());
        label.Pop();
        label.Pop();
        label.Pop();
        return label;
    }
}