using Godot;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetDiagnostic(SharpIdeDiagnostic diagnostic)
    {
        var label = new RichTextLabel();
        label.PushColor(TextEditorDotnetColoursDark.White);
        label.PushFontSize(14);
        label.AddText(diagnostic.Diagnostic.GetMessage());
        label.Pop();
        return label;
    }
}