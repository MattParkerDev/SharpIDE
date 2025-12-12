using System.Diagnostics;
using System.Reflection;
using Ardalis.GuardClauses;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpIDE.Application.Features.Debugging.Signing;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Debugging;

#pragma warning disable VSTHRD101
public class DebuggingService
{
	private DebugProtocolHost _debugProtocolHost = null!;
	public async Task Attach(int debuggeeProcessId, string? debuggerExecutablePath, Dictionary<SharpIdeFile, List<Breakpoint>> breakpointsByFile, SharpIdeProjectModel project, CancellationToken cancellationToken = default)
	{
		Guard.Against.NegativeOrZero(debuggeeProcessId, nameof(debuggeeProcessId), "Process ID must be a positive integer.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		if (string.IsNullOrWhiteSpace(debuggerExecutablePath))
		{
			throw new ArgumentNullException(nameof(debuggerExecutablePath), "Debugger executable path cannot be null or empty.");
		}

		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				//FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
				FileName = debuggerExecutablePath,
				Arguments = "--interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		process.Start();

		var debugProtocolHost = new DebugProtocolHost(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, false);
		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_debugProtocolHost = debugProtocolHost;
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
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // The VS Code Debug Protocol throws if you try to send a request from the dispatcher thread
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

			if (@event.Reason is StoppedEvent.ReasonValue.Exception)
			{
				Console.WriteLine("Stopped due to exception, continuing");
				var continueRequest = new ContinueRequest { ThreadId = @event.ThreadId!.Value };
				_debugProtocolHost.SendRequestSync(continueRequest);
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

		new DiagnosticsClient(debuggeeProcessId).ResumeRuntime();
		var configurationDoneRequest = new ConfigurationDoneRequest();
		debugProtocolHost.SendRequestSync(configurationDoneRequest);
	}
	// Typically you would do attachRequest, setBreakpointsRequest, configurationDoneRequest, then ResumeRuntime. But netcoredbg blows up on configurationDoneRequest if ResumeRuntime hasn't been called yet.

	public async Task StepOver(int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var nextRequest = new NextRequest(threadId);
		_debugProtocolHost.SendRequestSync(nextRequest);
	}
	public async Task StepInto(int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var stepInRequest = new StepInRequest(threadId);
		_debugProtocolHost.SendRequestSync(stepInRequest);
	}
	public async Task StepOut(int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var stepOutRequest = new StepOutRequest(threadId);
		_debugProtocolHost.SendRequestSync(stepOutRequest);
	}
	public async Task Continue(int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var continueRequest = new ContinueRequest(threadId);
		_debugProtocolHost.SendRequestSync(continueRequest);
	}

	public async Task<List<ThreadModel>> GetThreadsAtStopPoint()
	{
		var threadsRequest = new ThreadsRequest();
		var threadsResponse = _debugProtocolHost.SendRequestSync(threadsRequest);
		var mappedThreads = threadsResponse.Threads.Select(s => new ThreadModel
		{
			Id = s.Id,
			Name = s.Name
		}).ToList();
		return mappedThreads;
	}

	public async Task<List<StackFrameModel>> GetStackFramesForThread(int threadId)
	{
		var stackTraceRequest = new StackTraceRequest { ThreadId = threadId };
		var stackTraceResponse = _debugProtocolHost.SendRequestSync(stackTraceRequest);
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

	public async Task<List<Variable>> GetVariablesForStackFrame(int frameId)
	{
		var scopesRequest = new ScopesRequest { FrameId = frameId };
		var scopesResponse = _debugProtocolHost.SendRequestSync(scopesRequest);
		var allVariables = new List<Variable>();
		foreach (var scope in scopesResponse.Scopes)
		{
			var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
			var variablesResponse = _debugProtocolHost.SendRequestSync(variablesRequest);
			allVariables.AddRange(variablesResponse.Variables);
		}
		return allVariables;
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
