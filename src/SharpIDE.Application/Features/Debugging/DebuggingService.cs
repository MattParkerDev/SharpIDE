using System.Diagnostics;
using System.Reflection;
using Ardalis.GuardClauses;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpIDE.Application.Features.Debugging.Experimental.VsDbg;
using SharpIDE.Application.Features.Events;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Debugging;

public class DebuggingService
{
	private DebugProtocolHost _debugProtocolHost = null!;
	public async Task Attach(int debuggeeProcessId, Dictionary<SharpIdeFile, List<Breakpoint>> breakpointsByFile, CancellationToken cancellationToken = default)
	{
		Guard.Against.NegativeOrZero(debuggeeProcessId, nameof(debuggeeProcessId), "Process ID must be a positive integer.");
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				//FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
				FileName = @"C:\Users\Matthew\.vscode-insiders\extensions\ms-dotnettools.csharp-2.90.51-win32-x64\.debugger\x86_64\vsdbg.exe",
				Arguments = "--interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		process.Start();

		var debugProtocolHost = new DebugProtocolHost(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, false);
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
		debugProtocolHost.RegisterEventType<ExitedEvent>(async void (@event) =>
		{
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // The VS Code Debug Protocol throws if you try to send a request from the dispatcher thread
			debugProtocolHost.SendRequestSync(new DisconnectRequest());
		});
		debugProtocolHost.RegisterClientRequestType<HandshakeRequest, HandshakeArguments, HandshakeResponse>(responder =>
		{
			var signatureResponse = DebuggerHandshakeSigner.Sign(responder.Arguments.Value);
			responder.SetResponse(new HandshakeResponse(signatureResponse));
		});
		debugProtocolHost.RegisterEventType<StoppedEvent>(async void (@event) =>
		{
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); // The VS Code Debug Protocol throws if you try to send a request from the dispatcher thread
			//Dictionary<string, JToken>? test = @event.AdditionalProperties;
			var prop = @event.GetType().GetProperty("AdditionalProperties", BindingFlags.NonPublic | BindingFlags.Instance);
			// source, line, column
			var dict = prop?.GetValue(@event) as Dictionary<string, JToken>;
			var filePath = dict?["source"]?["path"]!.Value<string>()!;
			var line = (dict?["line"]?.Value<int>()!).Value;
			var executionStopInfo = new ExecutionStopInfo { FilePath = filePath, Line = line, ThreadId = @event.ThreadId!.Value };
			GlobalEvents.InvokeDebuggerExecutionStopped(executionStopInfo);
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
	// Typically you would do attachRequest, configurationDoneRequest, setBreakpointsRequest, then ResumeRuntime. But netcoredbg blows up on configurationDoneRequuest if ResumeRuntime hasn't been called yet.

	public async Task StepOver(int threadId, CancellationToken cancellationToken)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var nextRequest = new NextRequest(threadId);
		_debugProtocolHost.SendRequestSync(nextRequest);
	}

	public async Task<ThreadsStackTraceModel> GetInfoAtStopPoint()
	{
		var model = new ThreadsStackTraceModel();
		try
		{
			var threads = _debugProtocolHost.SendRequestSync(new ThreadsRequest());
			foreach (var thread in threads.Threads)
			{
				var threadModel = new ThreadModel { Id = thread.Id, Name = thread.Name };
				model.Threads.Add(threadModel);
				var stackTrace = _debugProtocolHost.SendRequestSync(new StackTraceRequest { ThreadId = thread.Id });
				var frame = stackTrace.StackFrames!.FirstOrDefault();
				if (frame == null) continue;
				var name = frame.Name;
				if (name == "[External Code]") continue; // TODO: handle this case

				// Infrastructure.dll!Infrastructure.DependencyInjection.AddInfrastructure(Microsoft.Extensions.DependencyInjection.IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration) Line 23
				// need to parse out the class name, method name, namespace, assembly name
				var methodName = name.Split('!')[1].Split('(')[0];
				var className = methodName.Split('.').Reverse().Skip(1).First();
				var namespaceName = string.Join('.', methodName.Split('.').Reverse().Skip(2).Reverse());
				var assemblyName = name.Split('!')[0];
				methodName = methodName.Split('.').Reverse().First();
				var frameModel = new StackFrameModel
				{
					Id = frame.Id,
					Name = frame.Name,
					Line = frame.Line,
					Column = frame.Column,
					Source = frame.Source?.Path,
					ClassName = className,
					MethodName = methodName,
					Namespace = namespaceName,
					AssemblyName = assemblyName
				};
				threadModel.StackFrames.Add(frameModel);
				var scopes = _debugProtocolHost.SendRequestSync(new ScopesRequest { FrameId = frame.Id });
				foreach (var scope in scopes.Scopes)
				{
					var scopeModel = new ScopeModel { Name = scope.Name };
					frameModel.Scopes.Add(scopeModel);
					var variablesResponse = _debugProtocolHost.SendRequestSync(new VariablesRequest { VariablesReference = scope.VariablesReference });
					scopeModel.Variables = variablesResponse.Variables;
				}
			}
		}
		catch (Exception)
		{
			throw;
		}

		return model;
	}
}
