extern alias WorkspaceAlias;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Text;
using RazorCodeDocumentExtensions = WorkspaceAlias::Microsoft.AspNetCore.Razor.Language.RazorCodeDocumentExtensions;

namespace SharpIDE.RazorAccess;

public static class RazorAccessors
{
	public static (ImmutableArray<SharpIdeRazorClassifiedSpan>, SourceText Text, List<SharpIdeRazorSourceMapping>) GetClassifiedSpans(SourceText sourceText, SourceText importsSourceText, string razorDocumentFilePath, string projectDirectory)
	{

		var razorSourceDocument = RazorSourceDocument.Create(sourceText.ToString(), razorDocumentFilePath);
		var importsRazorSourceDocument = RazorSourceDocument.Create(importsSourceText.ToString(), "_Imports.razor");

		var projectEngine = RazorProjectEngine.Create(RazorConfiguration.Default, RazorProjectFileSystem.Create(projectDirectory),
			builder => { /* configure features if needed */ });

		//var razorCodeDocument = projectEngine.Process(razorSourceDocument, RazorFileKind.Component, [], []);
		var razorCodeDocument = projectEngine.ProcessDesignTime(razorSourceDocument, RazorFileKind.Component, [importsRazorSourceDocument], []);
		var razorCSharpDocument = razorCodeDocument.GetRequiredCSharpDocument();
		//var generatedSourceText = razorCSharpDocument.Text;

		//var filePath = razorCodeDocument.Source.FilePath.AssumeNotNull();
		//var razorSourceText = razorCodeDocument.Source.Text;
		var razorSpans = RazorCodeDocumentExtensions.GetClassifiedSpans(razorCodeDocument);

		//var sharpIdeSpans = MemoryMarshal.Cast<RazorCodeDocumentExtensions.ClassifiedSpan, SharpIdeRazorClassifiedSpan>(razorSpans);
		var sharpIdeSpans = razorSpans.Select(s => new SharpIdeRazorClassifiedSpan(s.Span.ToSharpIdeSourceSpan(), s.Kind.ToSharpIdeSpanKind())).ToList();

		return (sharpIdeSpans.ToImmutableArray(), razorCSharpDocument.Text, razorCSharpDocument.SourceMappings.Select(s => s.ToSharpIdeSourceMapping()).ToList());
	}

	// public static bool TryGetMappedSpans(
	// 	TextSpan span,
	// 	SourceText source,
	// 	RazorCSharpDocument output,
	// 	out LinePositionSpan linePositionSpan,
	// 	out TextSpan mappedSpan)
	// {
	// 	foreach (SourceMapping sourceMapping in output.SourceMappings)
	// 	{
	// 		TextSpan textSpan1 = sourceMapping.OriginalSpan.AsTextSpan();
	// 		TextSpan textSpan2 = sourceMapping.GeneratedSpan.AsTextSpan();
	// 		if (textSpan2.Contains(span))
	// 		{
	// 			int num1 = span.Start - textSpan2.Start;
	// 			int num2 = span.End - textSpan2.End;
	// 			if (num1 >= 0 && num2 <= 0)
	// 			{
	// 				mappedSpan = new TextSpan(textSpan1.Start + num1, textSpan1.End + num2 - (textSpan1.Start + num1));
	// 				linePositionSpan = source.Lines.GetLinePositionSpan(mappedSpan);
	// 				return true;
	// 			}
	// 		}
	// 	}
	// 	mappedSpan = new TextSpan();
	// 	linePositionSpan = new LinePositionSpan();
	// 	return false;
	// }

    // /// <summary>
    // /// Wrapper to avoid <see cref="MissingMethodException"/>s in the caller during JITing
    // /// even though the method is not actually called.
    // /// </summary>
    // [MethodImpl(MethodImplOptions.NoInlining)]
    // private static object GetFileKindFromPath(string filePath)
    // {
    //     return FileKinds.GetFileKindFromPath(filePath);
    // }
}
