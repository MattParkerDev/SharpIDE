using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private readonly Texture2D _csharpMethodIcon = ResourceLoader.Load<Texture2D>("uid://b17p18ijhvsep");
    private readonly Texture2D _csharpClassIcon = ResourceLoader.Load<Texture2D>("uid://b027uufaewitj");
    private readonly Texture2D _csharpInterfaceIcon = ResourceLoader.Load<Texture2D>("uid://bdwmkdweqvowt");
    private readonly Texture2D _localVariableIcon = ResourceLoader.Load<Texture2D>("uid://vwvkxlnvqqk3");
    private readonly Texture2D _fieldIcon = ResourceLoader.Load<Texture2D>("uid://c4y7d5m4upfju");
    private readonly Texture2D _propertyIcon = ResourceLoader.Load<Texture2D>("uid://y5pwrwwrjqmc");
    private readonly Texture2D _keywordIcon = ResourceLoader.Load<Texture2D>("uid://b0ujhoq2xg2v0");
    private readonly Texture2D _namespaceIcon = ResourceLoader.Load<Texture2D>("uid://bob5blfjll4h3");
    private readonly Texture2D _eventIcon = ResourceLoader.Load<Texture2D>("uid://c3upo3lxmgtls");
    private readonly Texture2D _enumIcon = ResourceLoader.Load<Texture2D>("uid://8mdxo65qepqv");
    private readonly Texture2D _delegateIcon = ResourceLoader.Load<Texture2D>("uid://c83pv25rdescy");
    // delegate

    private Texture2D? GetIconForCompletion(SymbolKind? symbolKind, TypeKind? typeKind, Accessibility? accessibility, bool isKeyword)
    {
        if (isKeyword) return _keywordIcon;
        var texture = (symbolKind, typeKind, accessibility) switch
        {
            (SymbolKind.Method, _, _) => _csharpMethodIcon,
            (_, TypeKind.Interface, _) => _csharpInterfaceIcon,
            (_, TypeKind.Enum, _) => _enumIcon,
            (_, TypeKind.Delegate, _) => _delegateIcon,
            (SymbolKind.NamedType, _, _) => _csharpClassIcon,
            (SymbolKind.Local, _, _) => _localVariableIcon,
            (SymbolKind.Field, _, _) => _fieldIcon,
            (SymbolKind.Property, _, _) => _propertyIcon,
            (SymbolKind.Namespace, _, _) => _namespaceIcon,
            (SymbolKind.Event, _, _) => _eventIcon,
            _ => null
        };    
        return texture;
    }
}