using System.Collections.Concurrent;
using Ardalis.GuardClauses;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpIDE.Application.Features.Debugging.Signing;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Debugging;

#pragma warning disable VSTHRD101
public class DebuggingService(ILogger<DebuggingService> logger)
{
	private readonly ConcurrentDictionary<DebuggerSessionId, DebugProtocolHost> _debugProtocolHosts = [];

	private readonly ILogger<DebuggingService> _logger = logger;

	/// <returns>The debugging session ID</returns>
	public async Task<DebuggerSessionId> Attach(int debuggeeProcessId, DebuggerExecutableInfo? debuggerExecutableInfo, Dictionary<SharpIdeFile, List<Breakpoint>> breakpointsByFile, SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		Guard.Against.NegativeOrZero(debuggeeProcessId, nameof(debuggeeProcessId), "Process ID must be a positive integer.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var (inputStream, outputStream, isNetCoreDbg) = DebuggerProcessStreamHelper.NewDebuggerProcessStreamsForInfo(debuggerExecutableInfo, _logger);

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
		debugProtocolHost.RegisterEventType<ExitedEvent>(async void (@event) =>
		{
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // The VS Code Debug Protocol throws if you try to send a request from the dispatcher thread
			debugProtocolHost.SendRequestSync(new DisconnectRequest());
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
				var additionalProperties = @event.AdditionalProperties;
				// source, line, column
				if (additionalProperties.Count is not 0)
				{
					var filePath = additionalProperties?["source"]?["path"]!.Value<string>()!;
					var line = (additionalProperties?["line"]?.Value<int>()!).Value;
					var executionStopInfo = new ExecutionStopInfo { FilePath = filePath, Line = line, ThreadId = @event.ThreadId!.Value, Project = project };
					GlobalEvents.Instance.DebuggerExecutionStopped.InvokeParallelFireAndForget(executionStopInfo);
				}
				else
				{
					// we need to get the top stack frame to find out where we are
					var stackTraceRequest = new StackTraceRequest { ThreadId = @event.ThreadId!.Value, StartFrame = 0, Levels = 1 };
					var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
					var topFrame = stackTraceResponse.StackFrames.Single();
					var filePath = topFrame.Source.Path;
					var line = topFrame.Line;
					var executionStopInfo = new ExecutionStopInfo { FilePath = filePath, Line = line, ThreadId = @event.ThreadId!.Value, Project = project };
					GlobalEvents.Instance.DebuggerExecutionStopped.InvokeParallelFireAndForget(executionStopInfo);
				}
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
				["console"] = "internalConsole", // integratedTerminal, externalTerminal, internalConsole
			}
		};
		debugProtocolHost.SendRequestSync(attachRequest);
		// AttachRequest -> HandshakeRequest -> InitializedEvent
		await initializedEventTcs.Task;

		foreach (var breakpoint in breakpointsByFile)
		{
			var setBreakpointsRequest = new SetBreakpointsRequest
			{
				Source = new Source { Path = breakpoint.Key.Path },
				Breakpoints = breakpoint.Value.Select(b => new SourceBreakpoint { Line = b.Line }).ToList()
			};
			var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		}

		// netcoredbg has (a bug?) differing behaviour here compared to sharpdbg, where ConfigurationDone will never return if the runtime is paused at startup
		// so if using netcoredbg, we must ResumeRuntime first
		// Note that this means breakpoints on e.g. the first line of Main may be missed. It would be ideal for netcoredbg to fix this behaviour.
		if (isNetCoreDbg)
		{
			new DiagnosticsClient(debuggeeProcessId).ResumeRuntime();
			var configurationDoneRequest = new ConfigurationDoneRequest();
			debugProtocolHost.SendRequestSync(configurationDoneRequest);
		}
		else
		{
			var configurationDoneRequest = new ConfigurationDoneRequest();
			debugProtocolHost.SendRequestSync(configurationDoneRequest);
			new DiagnosticsClient(debuggeeProcessId).ResumeRuntime();
		}
		var sessionId = new DebuggerSessionId(Guid.NewGuid());
		_debugProtocolHosts[sessionId] = debugProtocolHost;
		return sessionId;
	}

	public async Task CloseDebuggerSession(DebuggerSessionId debuggerSessionId)
	{
		if (_debugProtocolHosts.TryRemove(debuggerSessionId, out var debugProtocolHost))
		{
			debugProtocolHost.Stop();
		}
		else
		{
			throw new InvalidOperationException($"Attempted to close non-existent Debugger session with ID '{debuggerSessionId.Value}'");
		}
	}

	public async Task SetBreakpointsForFile(DebuggerSessionId debuggerSessionId, SharpIdeFile file, List<Breakpoint> breakpoints, CancellationToken cancellationToken = default)
	{
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = file.Path },
			Breakpoints = breakpoints.Select(b => new SourceBreakpoint { Line = b.Line }).ToList()
		};
		var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
	}

	public async Task StepOver(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var nextRequest = new NextRequest(threadId);
		debugProtocolHost.SendRequestSync(nextRequest);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget();
	}
	public async Task StepInto(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var stepInRequest = new StepInRequest(threadId);
		debugProtocolHost.SendRequestSync(stepInRequest);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget();
	}
	public async Task StepOut(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var stepOutRequest = new StepOutRequest(threadId);
		debugProtocolHost.SendRequestSync(stepOutRequest);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget();
	}
	public async Task Continue(DebuggerSessionId debuggerSessionId, int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var continueRequest = new ContinueRequest(threadId);
		debugProtocolHost.SendRequestSync(continueRequest);
		GlobalEvents.Instance.DebuggerExecutionContinued.InvokeParallelFireAndForget();
	}

	public async Task<List<ThreadModel>> GetThreadsAtStopPoint(DebuggerSessionId debuggerSessionId)
	{
		var threadsRequest = new ThreadsRequest();
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
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
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
		var stackFrames = stackTraceResponse.StackFrames;

		var mappedStackFrames = stackFrames!.Select(frame =>
		{
			var isExternalCode = frame.Name == "[External Code]";
			ManagedStackFrameInfo? managedStackFrameInfo = isExternalCode ? null : ParseStackFrameName(frame.Name);
			return new StackFrameModel
			{
				Id = frame.Id,
				Name = frame.Name,
				Line = frame.Line,
				Column = frame.Column,
				Source = frame.Source?.Path,
				IsExternalCode =  isExternalCode,
				ManagedInfo = managedStackFrameInfo,
			};
		}).ToList();
		return mappedStackFrames;
	}

	public async Task<List<Variable>> GetVariablesForStackFrame(DebuggerSessionId debuggerSessionId, int frameId)
	{
		var scopesRequest = new ScopesRequest { FrameId = frameId };
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
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
		var debugProtocolHost = _debugProtocolHosts[debuggerSessionId];
		var variablesRequest = new VariablesRequest { VariablesReference = variablesReference };
		var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		return variablesResponse.Variables;
	}

	// netcoredbg does not provide the stack frame name in this format, so don't use this if using netcoredbg
	private static ManagedStackFrameInfo? ParseStackFrameName(string name)
	{
		return null;
		var methodName = name.Split('!')[1].Split('(')[0];
		var className = methodName.Split('.').Reverse().Skip(1).First();
		var namespaceName = string.Join('.', methodName.Split('.').Reverse().Skip(2).Reverse());
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
