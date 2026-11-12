using SharpIDE.Application.Features.FileSystem;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.FileWatching;

public static class NewFileTemplates
{
	public static string CsharpFile(string className, string @namespace, string typeKeyword)
	{
		var text = $$"""
		           namespace {{@namespace}};

		           public {{typeKeyword}} {{className}}
		           {

		           }

		           """;
		return text;
	}

	public static string ComputeNamespace(SharpIdeFolder folder)
	{
		var names = new List<string>();
		SharpIdeFolder? current = folder;
		while (current is not null && current.Parent is SharpIdeFolder) // Not a root - including one the IDE moved into another folder
		{
			names.Add(current.Name.Value);
			current = current.Parent as SharpIdeFolder;
		}
		names.Reverse();
		return string.Join('.', names);
	}
}
