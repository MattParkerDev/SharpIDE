using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo.Presentation;
using Microsoft.CodeAnalysis.SignatureHelp;

namespace SharpIDE.Godot.Features.CodeEditor;

public static class MethodSignatureHelpTooltip
{
	private static readonly FontVariation MonospaceFont = ResourceLoader.Load<FontVariation>("uid://cctwlwcoycek7");
	private static readonly Color HrColour = new Color("4d4d4d");
	private static readonly Color SelectedBackgroundColour = new Color("2e436e");
	
	public static void WriteToMethodSignatureHelpLabel(RichTextLabel richTextLabel, SignatureHelpItems signatureHelpItems, EditorThemeColorSet editorThemeColorSet)
	{
		richTextLabel.PushTable(1);
		richTextLabel.SetTableColumnExpand(0, true, 1);
		for (var i = 0; i < signatureHelpItems.Items.Count; i++)
		{
			var item = signatureHelpItems.Items[i];
			richTextLabel.PushCell();
			var isSelectedItem = i == signatureHelpItems.SelectedItemIndex;
			if (isSelectedItem) richTextLabel.SetCellRowBackgroundColor(SelectedBackgroundColour, SelectedBackgroundColour);
			
			var prefixQuickInfoElements = item.PrefixDisplayParts.ToInteractiveTextElements(null);
			foreach (var quickInfoElement in prefixQuickInfoElements)
			{
				CompletionDescriptionTooltip.WriteQuickInfoElement(richTextLabel, quickInfoElement, editorThemeColorSet);
			}
			var parameters = item.Parameters;
			var paramSeparatorString = item.SeparatorDisplayParts.GetFullText();
			for (var j = 0; j < parameters.Length; j++)
			{
				var parameter = parameters[j];
				var parameterQuickInfoElements = parameter.DisplayParts.ToImmutableArray().ToInteractiveTextElements(null);
				foreach (var quickInfoElement in parameterQuickInfoElements)
				{
					var isCurrentParameter = j == signatureHelpItems.SemanticParameterIndex;
					CompletionDescriptionTooltip.WriteQuickInfoElement(richTextLabel, quickInfoElement, editorThemeColorSet, isCurrentParameter);
				}
				if (j < parameters.Length - 1)
				{
					richTextLabel.PushFont(MonospaceFont);
					richTextLabel.AppendText(paramSeparatorString);
					richTextLabel.Pop();
				}
			}
            
			var suffixQuickInfoElements = item.SuffixDisplayParts.ToInteractiveTextElements(null);
			foreach (var quickInfoElement in suffixQuickInfoElements)
			{
				CompletionDescriptionTooltip.WriteQuickInfoElement(richTextLabel, quickInfoElement, editorThemeColorSet);
			}
			richTextLabel.Newline();
			var documentationQuickInfoElements = item.DocumentationFactory(CancellationToken.None).ToImmutableArray().ToInteractiveTextElements(null);
			foreach (var quickInfoElement in documentationQuickInfoElements)
			{
				CompletionDescriptionTooltip.WriteQuickInfoElement(richTextLabel, quickInfoElement, editorThemeColorSet);
			}

			var selectedParameterIndex = signatureHelpItems.SemanticParameterIndex;
			var selectedParameter = parameters.ElementAtOrDefault(selectedParameterIndex);
			var parameterDocumentation = selectedParameter?.DocumentationFactory(CancellationToken.None).ToImmutableArray().ToInteractiveTextElements(null) ?? [];
			if (parameterDocumentation.IsDefaultOrEmpty is false)
			{
				richTextLabel.Newline();
				richTextLabel.PushFont(MonospaceFont);
				richTextLabel.PushColor(editorThemeColorSet.VariableBlue);
				richTextLabel.AppendText(selectedParameter!.Name);
				richTextLabel.Pop(); // color
				richTextLabel.AppendText(": ");
				richTextLabel.Pop(); // font
				foreach (var parameterDocumentationQuickInfoElement in parameterDocumentation)
				{
					CompletionDescriptionTooltip.WriteQuickInfoElement(richTextLabel, parameterDocumentationQuickInfoElement, editorThemeColorSet);
				}
			}
			richTextLabel.Pop(); // cell
			if (i < signatureHelpItems.Items.Count - 1)
			{
				// Simulate a HR between the cells
				richTextLabel.PushCell();
				richTextLabel.SetCellRowBackgroundColor(HrColour, HrColour);
				richTextLabel.SetCellSizeOverride(new Vector2(1, 1), new Vector2(1, 1));
				richTextLabel.Pop();
			}
		}
		richTextLabel.Pop(); // table
	}
}