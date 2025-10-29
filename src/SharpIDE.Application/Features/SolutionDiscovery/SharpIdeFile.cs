using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using R3;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.SolutionDiscovery;

public class SharpIdeFile : ISharpIdeNode, IChildSharpIdeNode, IFileOrFolder
{
	public required IExpandableSharpIdeNode Parent { get; set; }
	public required string Path { get; set; }
	public required string Name { get; set; }
	public bool IsRazorFile => Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
	public bool IsCsprojFile => Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
	public bool IsCshtmlFile => Path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
	public bool IsCsharpFile => Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	public bool IsRoslynWorkspaceFile => IsCsharpFile || IsRazorFile || IsCshtmlFile;
	public GitStatus GitStatus { get; set; } = GitStatus.Unaltered;
	public required ReactiveProperty<bool> IsDirty { get; init; }
	public required bool SuppressDiskChangeEvents { get; set; } // probably has concurrency issues
	public required DateTimeOffset? LastIdeWriteTime { get; set; }
	public EventWrapper<SharpIdeFileLinePosition?, Task> FileContentsChangedExternally { get; } = new((_) => Task.CompletedTask);
	public EventWrapper<Task> FileDeleted { get; } = new(() => Task.CompletedTask);

	[SetsRequiredMembers]
	internal SharpIdeFile(string fullPath, string name, IExpandableSharpIdeNode parent, ConcurrentBag<SharpIdeFile> allFiles)
	{
		Path = fullPath;
		Name = name;
		Parent = parent;
		IsDirty = new ReactiveProperty<bool>(false);
		SuppressDiskChangeEvents = false;
		allFiles.Add(this);
	}
}
