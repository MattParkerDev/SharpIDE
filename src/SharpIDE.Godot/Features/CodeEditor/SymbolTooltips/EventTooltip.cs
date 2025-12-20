using Godot;

using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetEventSymbolInfo(IEventSymbol symbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddAccessibilityModifier(symbol);
        label.AddEventModifier(symbol);
        label.AddEventTypeName(symbol);
        label.AddEventName(symbol);
        label.AddEventMethods(symbol);
        label.AddContainingNamespaceAndClass(symbol);
        label.Newline();
        label.Pop();
        label.AddDocs(symbol);
        label.Pop();
        return label;
    }

    public static void AddEventModifier(this RichTextLabel label, IEventSymbol symbol)
    {
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText("event ");
        label.Pop();
    }

    public static void AddEventTypeName(this RichTextLabel label, IEventSymbol symbol)
    {
        label.AddType(symbol.Type);
        label.AddText(" ");
    }

    public static void AddEventName(this RichTextLabel label, IEventSymbol symbol)
    {
        label.PushColor(CachedColors.White);
        label.AddText(symbol.Name);
        label.Pop();
    }

    public static void AddEventMethods(this RichTextLabel label, IEventSymbol symbol)
    {
        label.AddText(" { ");
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText("add");
        label.Pop();
        label.AddText("; ");
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText("remove");
        label.Pop();
        label.AddText("; }");
    }
}