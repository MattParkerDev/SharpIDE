using Godot;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.QuickInfo.Presentation;

namespace SharpIDE.Godot.Features.CodeEditor;

public static class CompletionDescriptionTooltip
{
	private static readonly FontVariation MonospaceFont = ResourceLoader.Load<FontVariation>("uid://cctwlwcoycek7");
	private static readonly FontVariation MonospaceBoldFont = ResourceLoader.Load<FontVariation>("uid://bix80oipv61ew");
	
	public static RichTextLabel WriteToCompletionDescriptionLabel(RichTextLabel label, CompletionDescription completionDescription, EditorThemeColorSet editorThemeColorSet)
	{
		var quickInfoElements = completionDescription.TaggedParts.ToInteractiveTextElements(null);
		label.PushColor(TextEditorDotnetColoursDark.White);
		foreach (var quickInfoElement in quickInfoElements)
		{
			WriteQuickInfoElement(label, quickInfoElement, editorThemeColorSet);
		}
		label.Pop(); // color
		return label;
	}

	public static void WriteQuickInfoElement(RichTextLabel label, QuickInfoElement quickInfoElement, EditorThemeColorSet editorThemeColorSet, bool bold = false)
	{
		switch (quickInfoElement)
		{
			case QuickInfoClassifiedTextElement classifiedTextElement:
				foreach (var quickInfoClassifiedTextRun in classifiedTextElement.Runs)
				{
					var colour = ClassificationToColorMapper.GetColorForClassification(editorThemeColorSet, quickInfoClassifiedTextRun.ClassificationTypeName);
					label.PushColor(colour);
					var isMonospace = quickInfoClassifiedTextRun.ClassificationTypeName is not ("text" or "whitespace");
					if (isMonospace) label.PushFont(bold ? MonospaceBoldFont : MonospaceFont);
					if (bold) label.PushUnderline();
					label.AddText(quickInfoClassifiedTextRun.Text);
					if (bold) label.Pop();
					label.Pop();
					if (isMonospace) label.Pop();
				}
				break;
			case QuickInfoContainerElement containerElement:
				foreach (var element in containerElement.Elements)
				{
					WriteQuickInfoElement(label, element, editorThemeColorSet);
					label.Newline();
				}
				break;
			case QuickInfoGlyphElement glyphElement:
				break;
			case QuickInfoOnTheFlyDocsElement onTheFlyDocsElement:
				break;
			default: throw new NotImplementedException();
		}
	}
}