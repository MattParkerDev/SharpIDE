using System.Diagnostics;
using System.IO.Pipelines;
using OmniSharp.Extensions.DebugAdapter.Client;
using OmniSharp.Extensions.DebugAdapter.Protocol.Events;
using OmniSharp.Extensions.DebugAdapter.Protocol.Models;
using OmniSharp.Extensions.DebugAdapter.Protocol.Requests;
using OmniSharp.Extensions.DebugAdapter.Server;
using OmniSharp.Extensions.JsonRpc.Client;

namespace SharpIDE.Application.Features.Debugging;

public class DebuggingService
{

	public async Task Test(CancellationToken cancellationToken = default)
	{
		await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
				Arguments = """  --interpreter=vscode -- "C:\Program Files\dotnet\dotnet.exe" "C:\Users\Matthew\Documents\Git\BlazorCodeBreaker\artifacts\bin\WebApi\debug\WebApi.dll" """,				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				UseShellExecute = false
			}
		};
		process.Start();

		var stoppedTcs = new TaskCompletionSource<StoppedEvent>();

		var client = DebugAdapterClient.Create(options =>
		{
			options.AdapterId = "coreclr";
			options.ClientId = "vscode";
			options.LinesStartAt1 = true;
			options.ColumnsStartAt1 = true;
			options.SupportsVariableType = true;
			options.SupportsVariablePaging = true;
			options.SupportsRunInTerminalRequest = true;
			options.PathFormat = PathFormat.Path;
			options
				.WithInput(process.StandardOutput.BaseStream)
				.WithOutput(process.StandardInput.BaseStream)
				.OnStarted(async (adapterClient, token) =>
				{
					Console.WriteLine("Started");
				})
				.OnCapabilities(async (capabilitiesEvent, token) =>
				{
					Console.WriteLine("Capabilities");
				})
				.OnBreakpoint(async (breakpointEvent, token) =>
				{
					Console.WriteLine($"Breakpoint hit: {breakpointEvent}");
				})
				.OnStopped(async (stoppedEvent, token) =>
				{
					Console.WriteLine($"Notification received: {stoppedEvent}");
					stoppedTcs.SetResult(stoppedEvent);
				})
				.OnTerminated(async (terminatedEvent, token) =>
				{
					Console.WriteLine($"Terminated: {terminatedEvent}");
				});
			//.OnNotification(EventNames., );
		});


		await client.Initialize(cancellationToken);
		var breakpointsResponse = await client.SetBreakpoints(new SetBreakpointsArguments
		{
			Source = new Source { Path = @"C:\Users\Matthew\Documents\Git\BlazorCodeBreaker\src\WebApi\Program.cs" },
			Breakpoints = new Container<SourceBreakpoint>(new SourceBreakpoint { Line = 7 })
		}, cancellationToken);
		var launchResponse = await client.Launch(new LaunchRequestArguments()
		{
			NoDebug = false,
			ExtensionData = new Dictionary<string, object>
			{
				//["program"] = @"""C:\Program Files\dotnet\dotnet.exe"" ""C:\Users\Matthew\Documents\Git\BlazorCodeBreaker\artifacts\bin\WebApi\debug\WebApi.dll""",
				//["cwd"] = @"C:\Users\Matthew\Documents\Git\BlazorCodeBreaker\artifacts\bin\WebApi\debug", // working directory
				//["stopAtEntry"] = true,
				//["env"] = new Dictionary<string, string> { { "ASPNETCORE_ENVIRONMENT", "Development" } }
			}
		}, cancellationToken);

		var configurationDoneResponse = await client.RequestConfigurationDone(new ConfigurationDoneArguments(), cancellationToken);

		var stoppedEvent = await stoppedTcs.Task;
		var threads = await client.RequestThreads(new ThreadsArguments(), cancellationToken);

		var currentThread = threads.Threads!.Single(s => s.Id == stoppedEvent.ThreadId);
		var stackTrace = await client.RequestStackTrace(new StackTraceArguments { ThreadId = currentThread.Id }, cancellationToken);
		var frame = stackTrace.StackFrames!.First();
		var scopes = await client.RequestScopes(new ScopesArguments { FrameId = frame.Id }, cancellationToken);
		var scope = scopes.Scopes.First();
		var variablesResponse = await client.RequestVariables(new VariablesArguments() {VariablesReference = scope.VariablesReference}, cancellationToken);
		var variable = variablesResponse.Variables!.Skip(1).First();
		var variables2Response = await client.RequestVariables(new VariablesArguments() {VariablesReference = variable.VariablesReference}, cancellationToken);
		var variable2 = variables2Response.Variables!.Single();
		var variables3Response = await client.RequestVariables(new VariablesArguments() {VariablesReference = variable2.VariablesReference}, cancellationToken);
		//var continueResponse = await client.RequestContinue(new ContinueArguments(){ThreadId = 1}, cancellationToken);
		//await Task.Delay(1000);
		//var test = await client.RequestNext(new NextArguments(), cancellationToken: cancellationToken);

		//var result = await client.RequestStepIn(new StepInArguments(), cancellationToken: cancellationToken);

		await process.WaitForExitAsync();
	}
}
