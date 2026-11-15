using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Search;

public class FindInFilesSearchResult
{
	public required SharpIdeFile File { get; set; }
	public required int Line { get; set; }
	public required int StartColumn { get; set; }
	public required string LineText { get; set; }
}
