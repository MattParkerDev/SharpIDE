using System.Text;
using Godot;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace SharpIDE.Godot.Features.Debug_.Tab.SubTabs;

public partial class ThreadsVariablesSubTab
{
    private static readonly Color VariableNameColor = new Color("f0ac81");
    private static readonly Color VariableWhiteColor = new Color("d4d4d4");
    private static readonly Color VariableTypeColor = new Color("70737a");
    
    private void DebuggerVariableCustomDraw(TreeItem treeItem, Rect2 rect)
    {
        var variable = _variableReferenceLookup.GetValueOrDefault(treeItem);
        if (variable is null) return;
        
        const int iconSize = 18;
        var icon = variable.PresentationHint?.Kind switch
        {
            VariablePresentationHint.KindValue.Data => _arrayElementIcon,
            VariablePresentationHint.KindValue.Property => _propertyIcon,
            VariablePresentationHint.KindValue.Class => _staticMembersIcon,
            _ => null
        };
        if (icon is null)
        {
            // unlike sharpdbg and presumably vsdbg, netcoredbg does not set PresentationHint for variables
            if (variable.Name == "Static members") icon = _staticMembersIcon; // Will not currently occur, as 'Static members' are not handled by this custom draw
            else icon = _fieldIcon;
        }

        var variableValueDisplayColour = variable switch
        {
            _ when variable.PresentationHint?.Attributes is { } attrs && (attrs & VariablePresentationHint.AttributesValue.FailedEvaluation) != 0 => TextEditorDotnetColoursDark.ErrorRed,
            { Value: "null" } => TextEditorDotnetColoursDark.KeywordBlue,
            { Value: "true" or "false" } => TextEditorDotnetColoursDark.KeywordBlue,
            { Type: "string" or "char" } => TextEditorDotnetColoursDark.LightOrangeBrown,
            { Type: "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double" or "decimal" } => TextEditorDotnetColoursDark.NumberGreen,
            { Type: "byte?" or "sbyte?" or "short?" or "ushort?" or "int?" or "uint?" or "long?" or "ulong?" or "nint?" or "nuint?" or "float?" or "double?" or "decimal?" } => TextEditorDotnetColoursDark.NumberGreen, // value here will never actually be null, as we handled "null" value above
            _ => VariableWhiteColor
        };

        var font = _variablesTree.GetThemeFont(ThemeStringNames.Font);
        var fontSize = _variablesTree.GetThemeFontSize(ThemeStringNames.FontSize);
        const float padding = 4.0f;

        var currentX = rect.Position.X + padding;
        var textYPos = rect.Position.Y + (rect.Size.Y + fontSize) / 2 - 2;

        var iconRect = new Rect2(currentX, rect.Position.Y + (rect.Size.Y - iconSize) / 2, iconSize, iconSize);
        _variablesTree.DrawTextureRect(icon, iconRect, false);
        currentX += iconSize + padding;

        _variablesTree.DrawString(font, new Vector2(currentX, textYPos), variable.Name, HorizontalAlignment.Left, -1, fontSize, VariableNameColor);
        var variableNameDrawnWidth = font.GetStringSize(variable.Name, HorizontalAlignment.Left, -1, fontSize).X;
        currentX += variableNameDrawnWidth + padding;
        const string equalsString = "=";
        _variablesTree.DrawString(font, new Vector2(currentX, textYPos), equalsString, HorizontalAlignment.Left, -1, fontSize, VariableWhiteColor);
        var equalsWidth = font.GetStringSize(equalsString, HorizontalAlignment.Left, -1, fontSize).X;
        currentX += equalsWidth + padding;
        var variableTypeDisplayString = $$"""{{{variable.Type}}}""";
        var isObjectType = variable.Value == variableTypeDisplayString; // e.g. classes value will be the class name wrapped in {}
        if (isObjectType is false)
        {
            variableTypeDisplayString = $$"""{{{GetObjectNameWithoutNamespace(variable.Type)}}}""";
            _variablesTree.DrawString(font, new Vector2(currentX, textYPos), variableTypeDisplayString, HorizontalAlignment.Left, -1, fontSize, VariableTypeColor);
            var variableTypeDrawnSize = font.GetStringSize(variableTypeDisplayString, HorizontalAlignment.Left, -1, fontSize).X;
            currentX += variableTypeDrawnSize + padding;
        }
        var variableValueDisplayString = isObjectType ? GetObjectNameWithoutNamespace(variable.Type) : variable.Value;
        _variablesTree.DrawString(font, new Vector2(currentX, textYPos), variableValueDisplayString, HorizontalAlignment.Left, -1, fontSize, variableValueDisplayColour);
    }
    
    private static string GetObjectNameWithoutNamespace(string fullTypeName)
    {
        var test = SyntaxFactory.ParseTypeName(fullTypeName);
        var stringBuilder = new StringBuilder();
        WriteType(test, stringBuilder);
        var displayString = stringBuilder.ToString();
        return displayString;
    }
    
    // ChatGPT
    private static void WriteType(TypeSyntax type, StringBuilder sb)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                sb.Append(id.Identifier.Text);
                break;

            case QualifiedNameSyntax q:
                // Only keep the rightmost name
                WriteType(q.Right, sb);
                break;

            case GenericNameSyntax g:
                sb.Append(g.Identifier.Text);
                sb.Append('<');

                for (var i = 0; i < g.TypeArgumentList.Arguments.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");

                    WriteType(g.TypeArgumentList.Arguments[i], sb);
                }

                sb.Append('>');
                break;

            case AliasQualifiedNameSyntax a:
                WriteType(a.Name, sb);
                break;

            case PredefinedTypeSyntax p:
                sb.Append(p.Keyword.Text); // int, string, etc.
                break;

            case NullableTypeSyntax n:
                WriteType(n.ElementType, sb);
                sb.Append('?');
                break;

            case ArrayTypeSyntax a:
                WriteType(a.ElementType, sb);
                sb.Append('[');
                sb.Append(',', a.RankSpecifiers[0].Rank - 1);
                sb.Append(']');
                break;

            case PointerTypeSyntax p:
                WriteType(p.ElementType, sb);
                sb.Append('*');
                break;

            default:
                sb.Append(type.ToString()); // fallback
                break;
        }
    }
}