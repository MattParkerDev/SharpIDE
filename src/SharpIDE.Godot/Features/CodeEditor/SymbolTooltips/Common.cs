using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Roslyn.Utilities;

namespace SharpIDE.Godot.Features.CodeEditor;

public static partial class SymbolInfoComponents
{
    private static readonly FontVariation MonospaceFont = ResourceLoader.Load<FontVariation>("uid://cctwlwcoycek7");
    
    public static Control GetUnknownTooltip(ISymbol symbol)
    {
        var label = new RichTextLabel();
        label.FitContent = true;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.PushColor(CachedColors.White);
        label.PushFont(MonospaceFont);
        label.AddText($"UNHANDLED SYMBOL TYPE: {symbol.GetType().Name} - please create an issue!");
        label.Newline();
        label.AddText(symbol.Kind.ToString());
        label.AddText(" ");
        label.AddText(symbol.Name);
        label.Newline();
        label.AddContainingNamespaceAndClass(symbol);
        label.Newline();
        label.Pop(); // font
        label.AddDocs(symbol);
        
        label.Pop();
        return label;
    }
    
    private static string GetAccessibilityString(this Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public ",
        Accessibility.Private => "private ",
        Accessibility.Protected => "protected ",
        Accessibility.Internal => "internal ",
        Accessibility.ProtectedOrInternal => "protected internal ",
        Accessibility.ProtectedAndInternal => "private protected ",
        Accessibility.NotApplicable => string.Empty,
        _ => "unknown "
    };

    private static void AddAccessibilityModifier(this RichTextLabel label, ISymbol methodSymbol)
    {
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText(methodSymbol.DeclaredAccessibility.GetAccessibilityString());
        label.Pop();
    }
    
    private static void AddOverrideModifier(this RichTextLabel label, ISymbol methodSymbol)
    {
        if (methodSymbol.IsOverride)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("override");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddAbstractModifier(this RichTextLabel label, ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface }) return;
        if (symbol.IsAbstract)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("abstract");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddVirtualModifier(this RichTextLabel label, ISymbol methodSymbol)
    {
        if (methodSymbol.IsVirtual)
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText("virtual");
            label.Pop();
            label.AddText(" ");
        }
    }
    
    private static void AddAttributes(this RichTextLabel label, ISymbol methodSymbol)
    {
        var attributes = methodSymbol.GetAttributes();
        if (attributes.Length is 0) return;
        foreach (var (index, attribute) in attributes.Index())
        {
            label.AddAttribute(attribute, true);
        }
    }
    
    private static void AddContainingNamespaceAndClass(this RichTextLabel label, ISymbol symbol)
    {
        if (symbol.ContainingNamespace is null || symbol.ContainingNamespace.IsGlobalNamespace) return; // might be wrong
        label.Newline();
        if (symbol.ContainingType is null)
        {
            label.AddText("in namespace ");
        }
        else
        {
            label.AddText("in class ");
        }
        var namespaces = symbol.ContainingNamespace.ToDisplayString().Split('.');
        label.PushMeta("TODO", RichTextLabel.MetaUnderline.OnHover);
        foreach (var (index, ns) in namespaces.Index())
        {
            label.PushColor(CachedColors.KeywordBlue);
            label.AddText(ns);
            label.Pop();
            if (index < namespaces.Length - 1) label.AddText(".");
        }
        if (symbol.ContainingType is not null)
        {
            label.AddText(".");
            label.PushColor(CachedColors.ClassGreen);
            label.AddText(symbol.ContainingType.Name);
            label.Pop();
        }
        label.Pop(); // meta
    }
    
    private static void AddAttribute(this RichTextLabel label, AttributeData attribute, bool newLines)
    {
        label.AddText("[");
        label.PushColor(CachedColors.ClassGreen);
        var displayString = attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        if (displayString?.EndsWith("Attribute") is true) displayString = displayString[..^9]; // remove last 9 chars
        label.AddText(displayString ?? "unknown");
        label.Pop();
        label.AddText("]");
        if (newLines) label.Newline();
        else label.AddText(" ");
    }

    // TODO: parse these types better?
    private static (string, Color) GetForMetadataName(string metadataName)
    {
        var typeChar = metadataName[0];
        var typeColour = typeChar switch
        {
            'N' => CachedColors.KeywordBlue,
            'T' => CachedColors.ClassGreen,
            'F' => CachedColors.White,
            'P' => CachedColors.White,
            'M' => CachedColors.Yellow,
            'E' => CachedColors.White,
            _ => CachedColors.Orange
        };
        var minimalTypeName = (typeChar, metadataName) switch
        {
            // T:Microsoft.Extensions.DependencyInjection.IServiceCollection
            // M:Microsoft.Extensions.DependencyInjection.OptionsBuilderExtensions.ValidateOnStart``1(Microsoft.Extensions.Options.OptionsBuilder{``0})
            // F:Namespace.TypeName.FieldName
            // P:Namespace.TypeName.PropertyName
            // E:Namespace.TypeName.EventName
            // N:Namespace.Name
            ('N', _) => metadataName.Split('.').Last(),
            ('T', _) => metadataName.Split('.').Last(),
            ('F', _) => metadataName.Split('.').Last(),
            ('P', _) => metadataName.Split('.').Last(),
            ('E', _) => metadataName.Split('.').Last(),
            ('M', var s) when s.Contains('(') => s[(s.Split('(')[0].LastIndexOf('.') + 1)..s.IndexOf('(')],
            ('M', var s) => s.Split('.').Last(),
            _ => metadataName
        };
        return (minimalTypeName, typeColour);
    }
    
    private static void AddXmlDocFragment(this RichTextLabel label, string xmlFragment)
    {
        if (string.IsNullOrWhiteSpace(xmlFragment)) return;
        XmlFragmentParser.ParseFragment(xmlFragment, static (reader, label) =>
        {
            if (reader.NodeType == System.Xml.XmlNodeType.Text)
            {
                label.AddText(reader.Value);
            }
            else if (reader is { NodeType: System.Xml.XmlNodeType.Element, Name: DocumentationCommentXmlNames.SeeElementName or DocumentationCommentXmlNames.SeeAlsoElementName })
            {
                var cref = reader.GetAttribute(DocumentationCommentXmlNames.CrefAttributeName);
                if (cref is not null)
                {
                    var (minimalTypeName, typeColour) = GetForMetadataName(cref);
                    label.PushMeta("TODO", RichTextLabel.MetaUnderline.OnHover);
                        label.PushColor(typeColour);
                            label.AddText(minimalTypeName);
                        label.Pop();
                    label.Pop(); // meta
                }
            }
            else if (reader is { NodeType: System.Xml.XmlNodeType.Element, Name: DocumentationCommentXmlNames.TypeParameterReferenceElementName })
            {
                var name = reader.GetAttribute(DocumentationCommentXmlNames.NameAttributeName);
                if (name is not null)
                {
                    label.PushColor(CachedColors.ClassGreen);
                        label.AddText(name);
                    label.Pop();
                }
            }
            else if (reader is { NodeType: System.Xml.XmlNodeType.Element, Name: DocumentationCommentXmlNames.ParameterReferenceElementName })
            {
                var name = reader.GetAttribute(DocumentationCommentXmlNames.NameAttributeName);
                if (name is not null)
                {
                    label.PushColor(CachedColors.VariableBlue);
                        label.AddText(name);
                    label.Pop();
                }
            }
            else if (reader is { NodeType: System.Xml.XmlNodeType.Element })
            {
                var nameOrCref =  reader.GetAttribute(DocumentationCommentXmlNames.CrefAttributeName) ?? reader.GetAttribute(DocumentationCommentXmlNames.NameAttributeName);
                if (nameOrCref is not null)
                {
                    label.PushColor(CachedColors.White);
                    label.AddText(nameOrCref);
                    label.Pop();
                }
            }

            reader.Read();
        }, label);
    }

    private static readonly Color HrColour = new Color("4d4d4d");
    private static void AddDocs(this RichTextLabel label, ISymbol symbol)
    {
        if (symbol.IsOverride) symbol = symbol.GetOverriddenMember()!;
        var xmlDocs = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlDocs)) return;
        label.AddHr(100, 1, HrColour);
        label.Newline();
        var docComment = DocumentationComment.FromXmlFragment(xmlDocs);
        if (docComment.SummaryText is not null)
        {
            label.AddXmlDocFragment(docComment.SummaryText.ReplaceLineEndings(" "));
            label.Newline();
        }
        label.PushTable(2);
        if (docComment.ParameterNames.Length is not 0)
        {
            label.PushCell();
            label.PushColor(CachedColors.Gray);
            label.AddText("Params: ");
            label.Pop();
            label.Pop();
            foreach (var (index, parameterName) in docComment.ParameterNames.Index())
            {
                var parameterText = docComment.GetParameterText(parameterName);
                if (parameterText is null) continue;
                label.PushCell();
                label.PushColor(CachedColors.VariableBlue);
                label.AddText(parameterName);
                label.Pop();
                label.AddText(" - ");
                label.AddXmlDocFragment(parameterText);
                label.Pop(); // cell
                if (index < docComment.ParameterNames.Length - 1)
                {
                    label.PushCell();
                    label.Pop();
                }
            }
        }

        if (docComment.TypeParameterNames.Length is not 0)
        {
            label.PushCell();
            label.PushColor(CachedColors.Gray);
            label.AddText("Type Params: ");
            label.Pop();
            label.Pop();
            foreach (var (index, typeParameterName) in docComment.TypeParameterNames.Index())
            {
                var typeParameterText = docComment.GetTypeParameterText(typeParameterName);
                if (typeParameterText is null) continue;
                label.PushCell();
                label.PushColor(CachedColors.ClassGreen);
                label.AddText(typeParameterName);
                label.Pop();
                label.AddText(" - ");
                label.AddXmlDocFragment(typeParameterText);
                label.Pop(); // cell
                if (index < docComment.TypeParameterNames.Length - 1)
                {
                    label.PushCell();
                    label.Pop();
                }
            }
        }
        if (docComment.ReturnsText is not null)
        {
            label.PushCell();
                label.PushColor(CachedColors.Gray);
                    label.AddText("Returns: ");
                label.Pop();
            label.Pop();
            label.PushCell();
                label.AddXmlDocFragment(docComment.ReturnsText);
            label.Pop(); // cell
        }

        if (docComment.ExceptionTypes.Length is not 0)
        {
            label.PushCell();
                label.PushColor(CachedColors.Gray);
                    label.AddText("Exceptions: ");
                label.Pop();
            label.Pop();
            foreach (var (index, exceptionTypeName) in docComment.ExceptionTypes.Index())
            {
                var exceptionText = docComment.GetExceptionTexts(exceptionTypeName).FirstOrDefault();
                if (exceptionText is null) continue;
                label.PushCell();
                label.PushColor(CachedColors.ClassGreen);
                label.AddText(exceptionTypeName.Split('.').Last());
                label.Pop();
                label.AddText(" - ");
                label.AddXmlDocFragment(exceptionText);
                label.Pop(); // cell
                if (index < docComment.ExceptionTypes.Length - 1)
                {
                    label.PushCell();
                    label.Pop();
                }
            }
        }

        if (docComment.RemarksText is not null)
        {
            label.PushCell();
                label.PushColor(CachedColors.Gray);
                    label.AddText("Remarks: ");
                label.Pop();
            label.Pop();
            label.PushCell();
            label.AddXmlDocFragment(docComment.RemarksText);
            label.Pop(); // cell
            label.PushCell();
            label.Pop();
        }
        
        label.Pop(); // table
    }
    
    private static void AddType(this RichTextLabel label, ITypeSymbol symbol)
    {
        _ = symbol switch
        {
            {SpecialType: not SpecialType.None} => label.AddSpecialType(symbol),
            INamedTypeSymbol namedTypeSymbol => label.AddNamedType(namedTypeSymbol),
            ITypeParameterSymbol typeParameterSymbol => label.AddTypeParameter(typeParameterSymbol),
            IArrayTypeSymbol arrayTypeSymbol => label.AddArrayType(arrayTypeSymbol),
            _ => label.AddUnknownType(symbol)
        };
    }
    
    private static RichTextLabel AddUnknownType(this RichTextLabel label, ITypeSymbol symbol)
    {
        label.PushColor(CachedColors.Orange);
        label.AddText("[UNKNOWN TYPE]");
        label.AddText(symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        label.Pop();
        return label;
    }
    
    private static RichTextLabel AddArrayType(this RichTextLabel label, IArrayTypeSymbol symbol)
    {
        label.AddType(symbol.ElementType);
        label.AddText("[]");
        return label;
    }

    private static RichTextLabel AddSpecialType(this RichTextLabel label, ITypeSymbol symbol)
    {
        label.PushColor(CachedColors.KeywordBlue);
        label.AddText(symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        label.Pop();
        return label;
    }
    
    private static RichTextLabel AddNamedType(this RichTextLabel label, INamedTypeSymbol symbol)
    {
        label.PushMeta("TODO", RichTextLabel.MetaUnderline.OnHover);
        var colour = symbol.GetSymbolColourByType();
        label.PushColor(colour);
        label.AddText(symbol.Name);
        label.Pop();
        if (symbol.TypeArguments.Length is not 0)
        {
            label.AddText("<");
            for (var i = 0; i < symbol.TypeArguments.Length; i++)
            {
                var typeArg = symbol.TypeArguments[i];
                label.AddType(typeArg);
                if (i < symbol.TypeArguments.Length - 1) label.AddText(", ");
            }
            label.AddText(">");
        }
        label.Pop(); // meta
        return label;
    }

    private static RichTextLabel AddTypeParameter(this RichTextLabel label, ITypeParameterSymbol symbol)
    {
        label.PushColor(CachedColors.ClassGreen);
        label.AddText(symbol.Name);
        label.Pop();
        return label;
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