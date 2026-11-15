using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SharpIDE.Application;

public static class SharpIdeOtel
{
	public static readonly ActivitySource Source = new("SharpIde");
	public static readonly Meter Meter = new("SharpIde");
}
