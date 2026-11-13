using System.Collections.Concurrent;
using Microsoft.Build.Framework;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost.DesignTime;

// Adapted from Roslyn's BuildHost DiagnosticLog and MSBuildDiagnosticLogger
// https://github.com/dotnet/roslyn/tree/main/src/Workspaces/MSBuild/BuildHost/MSBuild/Logging

/// The errors and warnings for one project's design-time build
public sealed class DiagnosticLog
{
	private readonly List<DiagnosticLogItemDto> _items = [];

	public bool HasErrors { get { lock (_items) return _items.Any(i => i.Kind is DiagnosticLogItemKindDto.Error); } }

	public void Add(DiagnosticLogItemKindDto kind, string message, string projectFilePath)
	{
		lock (_items) _items.Add(new DiagnosticLogItemDto(kind, message, projectFilePath));
	}

	public void Add(Exception exception, string projectFilePath) => Add(DiagnosticLogItemKindDto.Error, exception.Message, projectFilePath);

	public DiagnosticLogItemDto[] ToArray() { lock (_items) return [.. _items]; }
}

/// One logger for the build session - routes each error and warning to the log of the submission that raised it, as several projects build at once
public sealed class DesignTimeDiagnosticLogger : ILogger
{
	private readonly ConcurrentDictionary<int, DiagnosticLog> _logsBySubmissionId = new();
	private IEventSource? _eventSource;

	public string? Parameters { get; set; }
	public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;

	/// Call after PendBuildRequest assigns the SubmissionId, before the submission executes, so no event arrives before its log is registered
	public void RegisterLog(int submissionId, DiagnosticLog log)
	{
		if (_logsBySubmissionId.TryAdd(submissionId, log) is false) throw new InvalidOperationException($"A log is already registered for submission {submissionId}.");
	}

	public void UnregisterLog(int submissionId) => _logsBySubmissionId.TryRemove(submissionId, out _);

	public void Initialize(IEventSource eventSource)
	{
		_eventSource = eventSource;
		_eventSource.ErrorRaised += OnErrorRaised;
		_eventSource.WarningRaised += OnWarningRaised;
	}

	public void Shutdown()
	{
		if (_eventSource is null) return;
		_eventSource.ErrorRaised -= OnErrorRaised;
		_eventSource.WarningRaised -= OnWarningRaised;
		_eventSource = null;
		_logsBySubmissionId.Clear();
	}

	private void OnErrorRaised(object sender, BuildErrorEventArgs e) => AddLogItem(DiagnosticLogItemKindDto.Error, e.BuildEventContext, e.ProjectFile, e.Message, e.File, e.LineNumber, e.ColumnNumber);
	private void OnWarningRaised(object sender, BuildWarningEventArgs e) => AddLogItem(DiagnosticLogItemKindDto.Warning, e.BuildEventContext, e.ProjectFile, e.Message, e.File, e.LineNumber, e.ColumnNumber);

	private void AddLogItem(DiagnosticLogItemKindDto kind, BuildEventContext? buildEventContext, string? projectFile, string? message, string? file, int lineNumber, int columnNumber)
	{
		// Always raised in the context of a submission whose log was registered before it executed - if not, there's nothing to attribute it to
		if (buildEventContext is null || _logsBySubmissionId.TryGetValue(buildEventContext.SubmissionId, out var log) is false) return;
		log.Add(kind, $"{file}: ({lineNumber}, {columnNumber}): {message}", projectFile ?? "");
	}
}
