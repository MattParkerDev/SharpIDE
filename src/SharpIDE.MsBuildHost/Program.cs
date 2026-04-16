using Microsoft.Build.Locator;
using SharpIDE.MsBuildHost;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

// Use latest version - https://github.com/microsoft/MSBuildLocator/issues/81
var instance = MSBuildLocator.QueryVisualStudioInstances().MaxBy(s => s.Version);
if (instance is null) throw new InvalidOperationException("No MSBuild instances found");
MSBuildLocator.RegisterInstance(instance);

var inputStream = Console.OpenStandardInput();
var outputStream = Console.OpenStandardOutput();

var handler = new HeaderDelimitedMessageHandler(outputStream, inputStream, new JsonMessageFormatter());
var rpc = new JsonRpc(handler);

rpc.AddLocalRpcTarget<IRpcBuildService>(new RpcBuildService(), null);

rpc.StartListening();

await rpc.Completion;
