using System.Collections.Concurrent;
using Ardalis.GuardClauses;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpDbg.Application.Protocol;
using SharpIDE.Application.Features.Debugging.Signing;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Debugging;

#pragma warning disable VSTHRD101
public class DebuggingService(ILogger<DebuggingService> logger)
{
	private readonly ConcurrentDictionary<DebuggerSessionId, (DebugProtocolHost DebugProtocolHost, IDisposable DebuggerDisposable)> _debugProtocolHosts = [];

	private readonly ILogger<DebuggingService> _logger = logger;

	/// <returns>The debugging session ID</returns>
	public async Task<DebuggerSessionId> Attach(int debuggeeProcessId, DebuggerExecutableInfo? debuggerExecutableInfo, Dictionary<SharpIdeFile, List<Breakpoint>> breakpointsByFile, SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		Guard.Against.NegativeOrZero(debuggeeProcessId, nameof(debuggeeProcessId), "Process ID must be a positive integer.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var (inputStream, outputStream, isNetCoreDbg, debuggerDisposable) = DebuggerProcessStreamHelper.NewDebuggerProcessStreamsForInfo(debuggerExecutableInfo, _logger);

		var debugProtocolHost = new DebugProtocolHost(inputStream, outputStream, false);
		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		debugProtocolHost.LogMessage += (sender, args) =>
		{
			//Console.WriteLine($"Log message: {args.Message}");
		};
		debugProtocolHost.EventReceived += (sender, args) =>
		{
			Console.WriteLine($"Event received: {args.EventType}");
		};
		debugProtocolHost.DispatcherError += (sender, args) =>
		{
			Console.WriteLine($"Dispatcher error: {args.Exception}");
		};
		debugProtocolHost.RequestReceived += (sender, args) =>
		{
			Console.WriteLine($"Request received: {args.Command}");
		};
		debugProtocolHost.RegisterEventType<OutputEvent>(@event =>
		{
			;
		});
		debugProtocolHost.RegisterEventType<InitializedEvent>(@event =>
		{
			initializedEventTcs.SetResult();
		});
		debugProtocolHost.RegisterClientRequestType<HandshakeRequest, HandshakeArguments, HandshakeResponse>(async void (responder) =>
		{
			var signatureResponse = await DebuggerHandshakeSigner.Sign(responder.Arguments.Value);
			responder.SetResponse(new HandshakeResponse(signatureResponse));
		});
		debugProtocolHost.RegisterEventType<StoppedEvent>(async void (@event) =>
		{
			try
			{
				await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // The VS Code Debug Protocol throws if you try to send a request from the dispatcher thread
				if (@event.Reason is StoppedEvent.ReasonValue.Exception)
				{
					Console.WriteLine("Stopped due to exception, continuing");
					var continueRequest = new ContinueRequest { ThreadId = @event.ThreadId!.Value };
					debugProtocolHost.SendRequestSync(continueRequest);
					return;
				}
				var executionStopInfo = new ExecutionStopInfo { ThreadId = @event.ThreadId!.Value, Project = project };
				GlobalEvents.Instance.DebuggerExecutionStopped.InvokeParallelFireAndForget(executionStopInfo);
			}
			catch (Exception e)
			{
				// TODO: use logger once this class is DI'd
				Console.WriteLine($"Error handling StoppedEvent: {e}");
			}
		});
		debugProtocolHost.VerifySynchronousOperationAllowed();
		var initializeRequest = new InitializeRequest
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
			SupportsHandshakeRequest = true
		};
		debugProtocolHost.Run();
		var response = debugProtocolHost.SendRequestSync(initializeRequest);

		var attachRequest = new AttachRequest
		{
			ConfigurationProperties = new Dictionary<string, JToken>
			{
				["name"] = "AttachRequestName",
				["type"] = "coreclr",
				["processId"] = debuggeeProcessId,
				["justMyCode"] = false,
				["console"] = "internalConsole", // integratedTerminal, externalTerminal, internalConsole
			}
		};
		debugProtocolHost.SendRequestSync(attachRequest);
		// AttachRequest -> HandshakeRequest -> InitializedEvent
		await initializedEventTcs.Task;

		foreach (var breakpoint in breakpointsByFile)
		{
			var file = breakpoint.Key;
			var path = file.IsMetadataAsSourceFile ? file.PdbSourceFilePathForDebugger ?? throw new ArgumentNullException(nameof(file.PdbSourceFilePathForDebugger)) : file.Path;
			var setBreakpointsRequest = new SetBreakpointsRequest
			{
				Source = new Source { Path = path },
				Breakpoints = breakpoint.Value.Select(b => new SourceBreakpoint { Line = b.Line }).ToList()
			};
			var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		}

		// netcoredbg has (a bug?) differing behaviour here compared to sharpdbg, where ConfigurationDone will never return if the runtime is paused at startup
		// so if using netcoredbg, we must ResumeRuntime first
		// Note that this means breakpoints on e.g. the first line of Main may be missed. It would be ideal for netcoredbg to fix this behaviour.
		if (isNetCoreDbg)
		{
			await DiagnosticClientResumeRuntime(debuggeeProcessId);
			var configurationDoneRequest = new ConfigurationDoneRequest();
			debugProtocolHost.SendRequestSync(configurationDoneRequest);
		}
		else
		{
			var configurationDoneRequest = new ConfigurationDoneRequest();
			debugProtocolHost.SendRequestSync(configurationDoneRequest);
			await DiagnosticClientResumeRuntime(debuggeeProcessId);
		}
		var sessionId = new DebuggerSessionId(Guid.NewGuid());
		_debugProtocolHosts[sessionId] = (debugProtocolHost, debuggerDisposable);
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
		if (_debugProtocolHosts.TryRemove(debuggerSessionId, out var s))
		{
			var (debugProtocolHost, debuggerDisposable) = s;
			debugProtocolHost.SendRequestSync(new DisconnectRequest());
			debugProtocolHost.Stop();
			debugProtocolHost.WaitForReader();
			debuggerDisposable.Dispose();
		}
		else
		{
			throw new InvalidOperationException($"Attempted to close non-existent Debugger session with ID '{debuggerSessionId.Value}'");
		}
	}

	public async Task SetBreakpointsForFile(DebuggerSessionId debuggerSessionId, SharpIdeFile file, List<Breakpoint> breakpoints, CancellationToken cancellationToken = default)
	{
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var path = file.IsMetadataAsSourceFile ? file.PdbSourceFilePathForDebugger ?? throw new ArgumentNullException(nameof(file.PdbSourceFilePathForDebugger)) : file.Path;
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = path },
			Breakpoints = breakpoints.Select(b => new SourceBreakpoint { Line = b.Line }).ToList()
		};
		var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
	}

	public async Task StepOver(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var nextRequest = new NextRequest(threadId);
		debugProtocolHost.SendRequestSync(nextRequest);
	}
	public async Task StepInto(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var stepInRequest = new StepInRequest(threadId);
		debugProtocolHost.SendRequestSync(stepInRequest);
	}
	public async Task StepOut(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var stepOutRequest = new StepOutRequest(threadId);
		debugProtocolHost.SendRequestSync(stepOutRequest);
	}
	public async Task Continue(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var continueRequest = new ContinueRequest(threadId);
		debugProtocolHost.SendRequestSync(continueRequest);
	}

	public async Task<List<ThreadModel>> GetThreadsAtStopPoint(DebuggerSessionId debuggerSessionId)
	{
		var threadsRequest = new ThreadsRequest();
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var threadsResponse = debugProtocolHost.SendRequestSync(threadsRequest);
		var mappedThreads = threadsResponse.Threads.Select(s => new ThreadModel
		{
			Id = s.Id,
			Name = s.Name
		}).ToList();
		return mappedThreads;
	}

	public async Task<List<StackFrameModel>> GetStackFramesForThread(DebuggerSessionId debuggerSessionId, int threadId)
	{
		var stackTraceRequest = new StackTraceRequest { ThreadId = threadId };
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
		var stackFrames = stackTraceResponse.StackFrames;

		var mappedStackFrames = stackFrames!.Select(frame => MapStackFrame(frame, threadId)).ToList();
		return mappedStackFrames;
	}

	public async Task<StackFrameModel> ResolveStackFrame(DebuggerSessionId debuggerSessionId, StackFrameModel stackFrame)
	{
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var response = debugProtocolHost.SendRequestSync(new ResolveStackFrameRequest(stackFrame.Id));
		return MapStackFrame(response.StackFrame, stackFrame.ThreadId);
	}

	public async Task<List<Variable>> GetVariablesForStackFrame(DebuggerSessionId debuggerSessionId, int frameId)
	{
		var scopesRequest = new ScopesRequest { FrameId = frameId };
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);
		var allVariables = new List<Variable>();
		foreach (var scope in scopesResponse.Scopes)
		{
			var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
			var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
			allVariables.AddRange(variablesResponse.Variables);
		}
		return allVariables;
	}

	public async Task<List<Variable>> GetVariablesForVariablesReference(DebuggerSessionId debuggerSessionId, int variablesReference)
	{
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var variablesRequest = new VariablesRequest { VariablesReference = variablesReference };
		var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		return variablesResponse.Variables;
	}

	public async Task<EvaluateResponse> EvaluateExpression(DebuggerSessionId debuggerSessionId, int frameId, string expression)
	{
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId].DebugProtocolHost;
		var evaluateRequest = new EvaluateRequest(expression)
		{
			FrameId = frameId,
			Context = EvaluateArguments.ContextValue.Watch,
		};
		return debugProtocolHost.SendRequestSync(evaluateRequest);
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
