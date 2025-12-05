using System.Diagnostics.CodeAnalysis;

namespace SharpIDE.Application.Features.Analysis;

public record struct SharpIdeFileLinePosition
{
	[SetsRequiredMembers]
	public SharpIdeFileLinePosition(int line, int column)
	{
		Line = line;
		Column = column;
	}
	public required int Line { get; set; }
	public required int Column { get; set; }
}
