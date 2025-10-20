namespace SharpIDE.Application.Features.FileWatching;

public static class NewFileTemplates
{
	public static string CsharpClass(string className, string @namespace)
	{
		var text = $$"""
		           namespace {{@namespace}};

		           public class {{className}}
		           {

		           }
		           """;
		return text;
	}
}
