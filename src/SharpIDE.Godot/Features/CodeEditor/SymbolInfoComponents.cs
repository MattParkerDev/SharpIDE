using Godot;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public static class SymbolInfoComponents
{
    private static readonly FontVariation MonospaceFont = ResourceLoader.Load<FontVariation>("uid://cctwlwcoycek7");
    public static RichTextLabel GetMethodSymbolInfo(IMethodSymbol methodSymbol)
    {
        var label = new RichTextLabel();
        label.FitContent = true;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.PushFont(MonospaceFont);
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText(methodSymbol.DeclaredAccessibility.GetAccessibilityString());
        label.Pop();
        label.AddText(" ");
        label.AddStaticModifier(methodSymbol);
        label.AddText(" ");
        label.AddMethodReturnType(methodSymbol);
        label.AddText(" ");
        label.AddMethodName(methodSymbol);
        label.AddTypeParameters(methodSymbol);
        label.AddText("(");
        label.AddParameters(methodSymbol);
        label.AddText(")");
        label.Newline();
        label.AddText("in class ");
        label.AddContainingNamespaceAndClass(methodSymbol);
        label.Newline(); // TODO: Make this only 1.5 lines high
        label.Newline(); //
        label.AddTypeParameterArguments(methodSymbol);
        label.AddHr(100, 1, CachedColors.Gray);
        label.Newline();
        label.Pop(); // font
        label.AddText("docs");

        return label;
    }
    
    private static string GetAccessibilityString(this Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "unknown"
    };
    
    private static void AddStaticModifier(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsStatic || methodSymbol.ReducedFrom?.IsStatic is true)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("static");
            label.Pop();
        }
    }
    
    private static void AddMethodReturnType(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.ReturnsVoid)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("void");
            label.Pop();
            return;
        }

        label.PushColor(CachedColors.ClassGreen);
        label.AddText(methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        label.Pop();
    }
    
    private static void AddMethodName(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        label.PushColor(CachedColors.Yellow);
        label.AddText(methodSymbol.Name);
        label.Pop();
    }
    
    private static void AddTypeParameters(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.TypeParameters.Length == 0) return;
        label.PushColor(CachedColors.White);
        label.AddText("<");
        label.Pop();
        foreach (var (index, typeParameter) in methodSymbol.TypeParameters.Index())
        {
            label.PushColor(CachedColors.ClassGreen);
            label.AddText(typeParameter.Name);
            label.Pop();
            if (index < methodSymbol.TypeParameters.Length - 1)
            {
                label.AddText(", ");
            }
        }
        label.PushColor(CachedColors.White);
        label.AddText(">");
        label.Pop();
    }
    
    private static void AddParameters(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsExtensionMethod)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("this");
            label.Pop();
            label.AddText(" ");
        }
        foreach (var (index, parameterSymbol) in methodSymbol.Parameters.Index())
        {
            var attributes = parameterSymbol.GetAttributes();
            if (attributes.Length is not 0)
            {
                foreach (var (attrIndex, attribute) in attributes.Index())
                {
                    label.AddText("[");
                    label.PushColor(CachedColors.ClassGreen);
                    var displayString = attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    if (displayString?.EndsWith("Attribute") is true) displayString = displayString[..^9]; // remove last 9 chars
                    label.AddText(displayString ?? "unknown");
                    label.Pop();
                    label.AddText("]");
                    label.AddText(" ");
                }
            }
            if (parameterSymbol.RefKind != RefKind.None) // ref, in, out
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText(parameterSymbol.RefKind.ToString().ToLower());
                label.Pop();
                label.AddText(" ");
            }
            else if (parameterSymbol.IsParams)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("params");
                label.Pop();
                label.AddText(" ");
            }
            label.PushColor(parameterSymbol.Type.GetSymbolColourByType());
            label.AddText(parameterSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            label.Pop();
            label.AddText(" ");
            label.PushColor(CachedColors.VariableBlue);
            label.AddText(parameterSymbol.Name);
            label.Pop();
            // default value
            if (parameterSymbol.HasExplicitDefaultValue)
            {
                label.AddText(" = ");
                if (parameterSymbol.ExplicitDefaultValue is null)
                {
                    label.PushColor(CachedColors.KeywordBlue);
                    label.AddText("null");
                    label.Pop();
                }
                else if (parameterSymbol.Type.TypeKind == TypeKind.Enum)
                {
                    var explicitDefaultValue = parameterSymbol.ExplicitDefaultValue;
                    // Find the enum field with the same constant value
                    var enumMember = parameterSymbol.Type.GetMembers()
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, explicitDefaultValue));

                    if (enumMember != null)
                    {
                        label.PushColor(CachedColors.InterfaceGreen);
                        label.AddText(parameterSymbol.Type.Name);
                        label.Pop();
                        label.PushColor(CachedColors.White);
                        label.AddText(".");
                        label.Pop();
                        label.PushColor(CachedColors.White);
                        label.AddText(enumMember.Name);
                        label.Pop();
                    }
                    else
                    {
                        label.PushColor(CachedColors.InterfaceGreen);
                        label.AddText(parameterSymbol.Type.Name);
                        label.Pop();
                        label.AddText($"({explicitDefaultValue})");
                    }
                }
                else if (parameterSymbol.ExplicitDefaultValue is string str)
                {
                    label.PushColor(CachedColors.LightOrangeBrown);
                    label.AddText($"""
                                   "{str}"
                                   """);
                    label.Pop();
                }
                else if (parameterSymbol.ExplicitDefaultValue is bool b)
                {
                    label.PushColor(CachedColors.KeywordBlue);
                    label.AddText(b ? "true" : "false");
                    label.Pop();
                }
                else
                {
                    label.AddText(parameterSymbol.ExplicitDefaultValue.ToString() ?? "unknown");
                }
            }

            if (index < methodSymbol.Parameters.Length - 1)
            {
                label.AddText(", ");
            }
        }
    }
    
    private static void AddContainingNamespaceAndClass(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.ContainingNamespace is null || methodSymbol.ContainingNamespace.IsGlobalNamespace) return;
        var namespaces = methodSymbol.ContainingNamespace.ToDisplayString().Split('.');
        foreach (var ns in namespaces)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText(ns);
            label.Pop();
            label.AddText(".");
        }
        label.PushColor(CachedColors.ClassGreen);
        label.AddText(methodSymbol.ContainingType.Name);
        label.Pop();
    }
    
    private static void AddTypeParameterArguments(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.TypeArguments.Length == 0) return;
        var typeParameters = methodSymbol.TypeParameters;
        var typeArguments = methodSymbol.TypeArguments;
        if (typeParameters.Length != typeArguments.Length) throw new Exception("Type parameters and type arguments length mismatch.");
        foreach (var (index, (typeArgument, typeParameter)) in methodSymbol.TypeArguments.Zip(typeParameters).Index())
        {
            label.PushColor(CachedColors.ClassGreen);
            label.AddText(typeParameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            label.Pop();
            label.AddText(" is ");
            label.PushColor(typeArgument.GetSymbolColourByType());
            label.AddText(typeArgument.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            label.Pop();
            if (index < methodSymbol.TypeArguments.Length - 1)
            {
                label.Newline();
            }
        }
    }
    
    // TODO: handle arrays etc, where there are multiple colours in one type
    private static Color GetSymbolColourByType(this ITypeSymbol symbol)
    {
        Color colour = symbol switch
        {
            {SpecialType: not SpecialType.None} => CachedColors.KeywordBlue,
            INamedTypeSymbol namedTypeSymbol => namedTypeSymbol.TypeKind switch
            {
                TypeKind.Class => CachedColors.ClassGreen,
                TypeKind.Interface => CachedColors.InterfaceGreen,
                TypeKind.Struct => CachedColors.ClassGreen,
                TypeKind.Enum => CachedColors.InterfaceGreen,
                TypeKind.Delegate => CachedColors.ClassGreen,
                _ => CachedColors.Orange
            },
            _ => CachedColors.Orange
        };
        return colour;
    }
}