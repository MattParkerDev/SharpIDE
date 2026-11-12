using Microsoft.Build.Framework;

namespace SharpIDE.MsBuildHost;

/// Keeps the errors a build logs, as "NU1202: message"
public sealed class ErrorCollectingLogger : ILogger
{
	private readonly List<string> _errors = [];

	public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;
	public string? Parameters { get; set; }

	public List<string> Errors
	{
		get
		{
			lock (_errors) return _errors.Distinct().ToList();
		}
	}

	public void Initialize(IEventSource eventSource)
	{
		eventSource.ErrorRaised += (_, e) =>
		{
			var error = string.IsNullOrWhiteSpace(e.Code) ? e.Message ?? "Error" : $"{e.Code}: {e.Message}";
			lock (_errors) _errors.Add(error);
		};
	}

	public void Shutdown()
	{
	}
}
