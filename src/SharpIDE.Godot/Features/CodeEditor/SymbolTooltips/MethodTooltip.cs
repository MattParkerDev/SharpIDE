using Godot;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    public static RichTextLabel GetMethodSymbolInfo(IMethodSymbol methodSymbol)
    {
        var label = new RichTextLabel();
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddAttributes(methodSymbol);
        label.AddAccessibilityModifier(methodSymbol);
        label.AddMethodStaticModifier(methodSymbol);
        label.AddVirtualModifier(methodSymbol);
        label.AddAbstractModifier(methodSymbol);
        label.AddSealedModifier(methodSymbol);
        label.AddOverrideModifier(methodSymbol);
        label.AddMethodAsyncModifier(methodSymbol);
        label.AddMethodReturnType(methodSymbol);
        label.AddText(" ");
        label.AddMethodName(methodSymbol);
        label.AddTypeParameters(methodSymbol);
        label.AddText("(");
        label.AddParameters(methodSymbol);
        label.AddText(")");
        label.AddTypeParameterConstraints(methodSymbol.TypeParameters);
        label.AddContainingNamespaceAndClass(methodSymbol);
        label.Newline();
        label.AddTypeParameterArguments(methodSymbol);
        label.Pop(); // font
        label.AddDocs(methodSymbol);
        label.Pop(); // default white
        return label;
    }
    
    private static void AddMethodStaticModifier(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsStatic || methodSymbol.ReducedFrom?.IsStatic is true)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("static");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddMethodAsyncModifier(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsAsync)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("async");
            label.Pop();
            label.AddText(" ");
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
        
        label.AddType(methodSymbol.ReturnType);
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
        
        var parameters = methodSymbol.ReducedFrom?.Parameters ?? methodSymbol.Parameters;
        foreach (var (index, parameterSymbol) in parameters.Index())
        {
            var attributes = parameterSymbol.GetAttributes();
            if (attributes.Length is not 0)
            {
                foreach (var (attrIndex, attribute) in attributes.Index())
                {
                    label.AddAttribute(attribute, false);
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
            label.AddType(parameterSymbol.Type);
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

            if (index < parameters.Length - 1)
            {
                label.AddText(", ");
            }
        }
    }
    
    private static void AddTypeParameterArguments(this RichTextLabel label, IMethodSymbol methodSymbol)
    {
        if (methodSymbol.TypeArguments.Length == 0) return;
        label.Newline(); // TODO: Make this only 0.5 lines high
        var typeParameters = methodSymbol.TypeParameters;
        var typeArguments = methodSymbol.TypeArguments;
        if (typeParameters.Length != typeArguments.Length) throw new Exception("Type parameters and type arguments length mismatch.");
        foreach (var (index, (typeArgument, typeParameter)) in methodSymbol.TypeArguments.Zip(typeParameters).Index())
        {
            label.PushColor(CachedColors.ClassGreen);
            label.AddType(typeParameter);
            label.Pop();
            label.AddText(" is ");
            label.AddType(typeArgument);
            if (index < methodSymbol.TypeArguments.Length - 1)
            {
                label.Newline();
            }
        }
    }

    private static void AddTypeParameterConstraints(this RichTextLabel label, ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        foreach (var typeParameter in typeParameters)
        {
            var hasConstraints = typeParameter.HasReferenceTypeConstraint || typeParameter.HasValueTypeConstraint || typeParameter.HasUnmanagedTypeConstraint || typeParameter.HasNotNullConstraint || typeParameter.HasConstructorConstraint || typeParameter.AllowsRefLikeType || typeParameter.ConstraintTypes.Length > 0;
            if (hasConstraints is false) continue;
            
            label.AddText(" ");
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("where");
            label.Pop();
            label.AddText(" ");
            label.AddTypeParameter(typeParameter);
            label.AddText(" : ");

            if (typeParameter.HasReferenceTypeConstraint)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("class");
                label.Pop();
            }

            if (typeParameter.HasValueTypeConstraint)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("struct");
                label.Pop();
            }

            if (typeParameter.HasUnmanagedTypeConstraint)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("unmanaged");
                label.Pop();
            }

            if (typeParameter.HasNotNullConstraint)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("notnull");
                label.Pop();
            }

            foreach (var typeParameterConstraintType in typeParameter.ConstraintTypes)
            {
                label.AddType(typeParameterConstraintType);
            }

            if (typeParameter.HasConstructorConstraint)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("new()");
                label.Pop();
            }

            if (typeParameter.AllowsRefLikeType)
            {
                label.PushColor(CachedColors.KeywordBlue);
                label.AddText("allows ref struct");
                label.Pop();
            }
        }
    }
}
