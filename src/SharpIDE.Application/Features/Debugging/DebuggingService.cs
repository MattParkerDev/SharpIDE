using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Ardalis.GuardClauses;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using SharpDbg.Application.Protocol;
using SharpDbg.DapProtocol;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Debugging;

#pragma warning disable VSTHRD101
public class DebuggingService(ILogger<DebuggingService> logger)
{
	private readonly ConcurrentDictionary<DebuggerSessionId, (DapConnection Connection, IDisposable DebuggerDisposable)> _debugConnections = [];

	private readonly ILogger<DebuggingService> _logger = logger;

	/// <returns>The debugging session ID</returns>
	public async Task<DebuggerSessionId> Attach(int debuggeeProcessId, DebuggerExecutableInfo? debuggerExecutableInfo, Dictionary<SharpIdeFile, List<Breakpoint>> breakpointsByFile, SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		Guard.Against.NegativeOrZero(debuggeeProcessId, nameof(debuggeeProcessId), "Process ID must be a positive integer.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var (inputStream, outputStream, isNetCoreDbg, debuggerDisposable) = DebuggerProcessStreamHelper.NewDebuggerProcessStreamsForInfo(debuggerExecutableInfo, _logger);

		// inputStream is the debug adapter's stdin, outputStream is its stdout. The connection reads from the adapter's stdout, and writes to its stdin.
		var connection = new DapConnection(outputStream, inputStream, new DebuggerClientRequestHandler(), new DapConnectionOptions
		{
			Log = message => _logger.LogDebug("DAP: {Message}", message)
		});
		connection.RegisterRequest<HandshakeArguments>();
		connection.ProtocolError += exception => _logger.LogError(exception, "DAP protocol error");

		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.OnEvent<InitializedEvent>(_ => initializedEventTcs.TrySetResult());
		connection.OnEvent<StoppedEvent>(async void (@event) =>
		{
			try
			{
				if (@event.Reason == StoppedEvent.ReasonValue.Exception)
				{
					Console.WriteLine("Stopped due to exception, continuing");
					// Requests must not be sent synchronously from an event handler, as events and incoming requests share the dispatch loop
					await SendRequestAsync(connection, new ContinueArguments { ThreadId = @event.ThreadId!.Value });
					return;
				}
				var executionStopInfo = new ExecutionStopInfo { ThreadId = @event.ThreadId!.Value, Project = project };
				GlobalEvents.Instance.DebuggerExecutionStopped.InvokeParallelFireAndForget(executionStopInfo);
			}
			catch (Exception e)
			{
				_logger.LogError(e, "Error handling StoppedEvent");
			}
		});

		var initializeArguments = new InitializeArguments
		{
			ClientID = "vscode",
			ClientName = "Visual Studio Code",
			AdapterID = "coreclr",
			Locale = "en-us",
			LinesStartAt1 = true,
			ColumnsStartAt1 = true,
			PathFormat = InitializeArguments.PathFormatValue.Path,
			SupportsVariableType = true,
			SupportsVariablePaging = true,
			SupportsRunInTerminalRequest = true,
			AdditionalProperties = new JsonObject { ["supportsHandshakeRequest"] = true }
		};
		connection.Start();
		await SendRequestAsync(connection, initializeArguments, cancellationToken);

		var attachArguments = new AttachArguments
		{
			AdditionalProperties = new JsonObject
			{
				["name"] = "AttachRequestName",
				["type"] = "coreclr",
				["processId"] = debuggeeProcessId,
				["justMyCode"] = false,
				["console"] = "internalConsole", // integratedTerminal, externalTerminal, internalConsole
			}
		};
		await SendRequestAsync(connection, attachArguments, cancellationToken);
		// AttachRequest -> HandshakeRequest -> InitializedEvent
		await initializedEventTcs.Task;

		foreach (var (file, breakpoints) in breakpointsByFile)
		{
			await SendRequestAsync(connection, CreateSetBreakpointsArguments(file, breakpoints), cancellationToken);
		}

		// netcoredbg has (a bug?) differing behaviour here compared to sharpdbg, where ConfigurationDone will never return if the runtime is paused at startup
		// so if using netcoredbg, we must ResumeRuntime first
		// Note that this means breakpoints on e.g. the first line of Main may be missed. It would be ideal for netcoredbg to fix this behaviour.
		if (isNetCoreDbg)
		{
			await DiagnosticClientResumeRuntime(debuggeeProcessId);
			await SendRequestAsync(connection, new ConfigurationDoneArguments(), cancellationToken);
		}
		else
		{
			await SendRequestAsync(connection, new ConfigurationDoneArguments(), cancellationToken);
			await DiagnosticClientResumeRuntime(debuggeeProcessId);
		}
		var sessionId = new DebuggerSessionId(Guid.NewGuid());
		_debugConnections[sessionId] = (connection, debuggerDisposable);
		return sessionId;
	}

	// For applications like godot, which start their own CLR, diagnostics IPC may not be available immediately.
	// Retry until it succeeds.
	private static async Task DiagnosticClientResumeRuntime(int debuggeeProcessId)
	{
		var diagnosticsClient = new DiagnosticsClient(debuggeeProcessId);
		const int maxRetries = 5;
		var delayMs = 50;

		for (var attempt = 1; attempt <= maxRetries; attempt++)
		{
			try
			{
				await Task.Delay(delayMs);
				diagnosticsClient.ResumeRuntime();
				return;
			}
			catch (ServerNotAvailableException) when (attempt < maxRetries)
			{
				delayMs *= 2;
			}
		}
		diagnosticsClient.ResumeRuntime();
	}

	public async Task CloseDebuggerSession(DebuggerSessionId debuggerSessionId)
	{
		if (_debugConnections.TryRemove(debuggerSessionId, out var s))
		{
			var (connection, debuggerDisposable) = s;
			try
			{
				await SendRequestAsync(connection, new DisconnectArguments());
			}
			finally
			{
				connection.Stop();
				debuggerDisposable.Dispose();
			}
		}
		else
		{
			throw new InvalidOperationException($"Attempted to close non-existent Debugger session with ID '{debuggerSessionId.Value}'");
		}
	}

	public async Task SetBreakpointsForFile(DebuggerSessionId debuggerSessionId, SharpIdeFile file, List<Breakpoint> breakpoints, CancellationToken cancellationToken = default)
	{
		var connection = GetConnection(debuggerSessionId);
		await SendRequestAsync(connection, CreateSetBreakpointsArguments(file, breakpoints), cancellationToken);
	}

	private static SetBreakpointsArguments CreateSetBreakpointsArguments(SharpIdeFile file, List<Breakpoint> breakpoints)
	{
		var path = file.IsMetadataAsSourceFile ? file.PdbSourceFilePathForDebugger ?? throw new ArgumentNullException(nameof(file.PdbSourceFilePathForDebugger)) : file.Path;
		return new SetBreakpointsArguments
		{
			Source = new Source { Path = path },
			Breakpoints = breakpoints.Select(b => new SourceBreakpoint { Line = b.Line }).ToList()
		};
	}

	public async Task StepOver(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await SendRequestAsync(GetConnection(debuggerSessionId), new NextArguments { ThreadId = threadId }, cancellationToken);
	}
	public async Task StepInto(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await SendRequestAsync(GetConnection(debuggerSessionId), new StepInArguments { ThreadId = threadId }, cancellationToken);
	}
	public async Task StepOut(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await SendRequestAsync(GetConnection(debuggerSessionId), new StepOutArguments { ThreadId = threadId }, cancellationToken);
	}
	public async Task Continue(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await SendRequestAsync(GetConnection(debuggerSessionId), new ContinueArguments { ThreadId = threadId }, cancellationToken);
	}

	public async Task<List<ThreadModel>> GetThreadsAtStopPoint(DebuggerSessionId debuggerSessionId)
	{
		var threadsResponse = await SendRequestAsync(GetConnection(debuggerSessionId), new ThreadsArguments());
		var mappedThreads = threadsResponse.Threads.Select(s => new ThreadModel
		{
			Id = s.Id,
			Name = s.Name
		}).ToList();
		return mappedThreads;
	}

	public async Task<List<StackFrameModel>> GetStackFramesForThread(DebuggerSessionId debuggerSessionId, int threadId)
	{
		var stackTraceResponse = await SendRequestAsync(GetConnection(debuggerSessionId), new StackTraceArguments { ThreadId = threadId });
		var mappedStackFrames = stackTraceResponse.StackFrames.Select(frame => MapStackFrame(frame, threadId)).ToList();
		return mappedStackFrames;
	}

	public async Task<StackFrameModel> ResolveStackFrame(DebuggerSessionId debuggerSessionId, StackFrameModel stackFrame)
	{
		var response = await SendRequestAsync(GetConnection(debuggerSessionId), new ResolveStackFrameArguments { StackFrameId = stackFrame.Id });
		return MapStackFrame(response.StackFrame, stackFrame.ThreadId);
	}

	public async Task<List<Variable>> GetVariablesForStackFrame(DebuggerSessionId debuggerSessionId, int frameId)
	{
		var connection = GetConnection(debuggerSessionId);
		var scopesResponse = await SendRequestAsync(connection, new ScopesArguments { FrameId = frameId });
		var allVariables = new List<Variable>();
		foreach (var scope in scopesResponse.Scopes)
		{
			var variablesResponse = await SendRequestAsync(connection, new VariablesArguments { VariablesReference = scope.VariablesReference });
			allVariables.AddRange(variablesResponse.Variables);
		}
		return allVariables;
	}

	public async Task<List<Variable>> GetVariablesForVariablesReference(DebuggerSessionId debuggerSessionId, int variablesReference)
	{
		var variablesResponse = await SendRequestAsync(GetConnection(debuggerSessionId), new VariablesArguments { VariablesReference = variablesReference });
		return variablesResponse.Variables.ToList();
	}

	/// <exception cref="DebuggerRequestFailedException">The debugger failed to evaluate the expression.</exception>
	public async Task<EvaluateResponse> EvaluateExpression(DebuggerSessionId debuggerSessionId, int frameId, string expression)
	{
		var evaluateArguments = new EvaluateArguments
		{
			Expression = expression,
			FrameId = frameId,
			Context = EvaluateArguments.ContextValue.Watch,
		};
		return await SendRequestAsync(GetConnection(debuggerSessionId), evaluateArguments);
	}

	private DapConnection GetConnection(DebuggerSessionId debuggerSessionId) => _debugConnections[debuggerSessionId].Connection;

	/// <exception cref="DebuggerRequestFailedException">The debugger responded with an error.</exception>
	private static async Task<TResponse> SendRequestAsync<TResponse>(DapConnection connection, IDapRequest<TResponse> request, CancellationToken cancellationToken = default)
	{
		var result = await connection.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
		return result switch
		{
			DapError error => throw new DebuggerRequestFailedException(request.Command, error),
			TResponse response => response,
			null => throw new InvalidOperationException($"No result for request '{request.Command}'"),
		};
	}

	private static StackFrameModel MapStackFrame(StackFrame frame, int threadId)
	{
		var isExternalCode = frame.Name == "[External Code]";
		return new StackFrameModel
		{
			Id = frame.Id,
			ThreadId = threadId,
			Name = frame.Name,
			Line = frame.Line,
			Column = frame.Column,
			EndLine = frame.EndLine,
			EndColumn = frame.EndColumn,
			Source = frame.Source?.Path,
			IsExternalCode = isExternalCode,
			IsResolved = frame.IsResolved ?? true,
			DecompiledSourceInfo = frame.DecompiledSourceInfo,
			ManagedInfo = isExternalCode ? null : ParseStackFrameName(frame.Name),
		};
	}

	// netcoredbg does not provide the stack frame name in this format, so don't use this if using netcoredbg
	private static ManagedStackFrameInfo? ParseStackFrameName(string name)
	{
		return null;
		var methodName = name.Split('!')[1].Split('(')[0];
		var className = methodName.Split('.').Reverse().Skip(1).First();
		var namespaceName = methodName.Split('.').AsValueEnumerable().Reverse().Skip(2).Reverse().JoinToString(".");
		var assemblyName = name.Split('!')[0];
		methodName = methodName.Split('.').Reverse().First();
		var managedStackFrameInfo = new ManagedStackFrameInfo
		{
			MethodName = methodName,
			ClassName = className,
			Namespace = namespaceName,
			AssemblyName = assemblyName
		};
		return managedStackFrameInfo;
	}
}
