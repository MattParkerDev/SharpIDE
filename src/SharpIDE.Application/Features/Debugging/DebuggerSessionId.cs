using Ardalis.GuardClauses;

namespace SharpIDE.Application.Features.Debugging;

public readonly record struct DebuggerSessionId
{
	public readonly Guid Value;

	public DebuggerSessionId(Guid value)
	{
		if (value == Guid.Empty) throw new ArgumentException("DebuggerSessionId cannot be an empty Guid", nameof(value));
		Value = value;
	}
}
