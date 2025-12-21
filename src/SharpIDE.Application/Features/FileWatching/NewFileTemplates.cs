using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

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

	public static string ComputeNamespace(IFolderOrProject folder)
	{
		var names = new List<string>();
		IFolderOrProject? current = folder;
		while (current is not null)
		{
			names.Add(current.Name);
			current = current.Parent as IFolderOrProject;
		}
		names.Reverse();
		return string.Join('.', names);
	}
}
