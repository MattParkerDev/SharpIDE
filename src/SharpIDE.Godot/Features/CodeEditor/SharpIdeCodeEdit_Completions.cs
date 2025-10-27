using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private readonly Texture2D _csharpMethodIcon = ResourceLoader.Load<Texture2D>("uid://b17p18ijhvsep");
    private readonly Texture2D _csharpClassIcon = ResourceLoader.Load<Texture2D>("uid://b027uufaewitj");

    private Texture2D? GetIconForSymbolKind(SymbolKind? symbolKind)
    {
        var texture = symbolKind switch
        {
            SymbolKind.Method => _csharpMethodIcon,
            SymbolKind.NamedType => _csharpClassIcon,
            //SymbolKind.Local => ,
            //SymbolKind.Property => ,
            //SymbolKind.Field => ,
            _ => null
        };    
        return texture;
    }
}