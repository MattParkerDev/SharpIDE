using Microsoft.Build.Locator;
using PolyType.SourceGenerator;
using SharpIDE.MsBuildHost;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

// Use latest version - https://github.com/microsoft/MSBuildLocator/issues/81
var instance = MSBuildLocator.QueryVisualStudioInstances().MaxBy(s => s.Version);
if (instance is null) throw new InvalidOperationException("No MSBuild instances found");
MSBuildLocator.RegisterInstance(instance);

var inputStream = Console.OpenStandardInput();
var outputStream = Console.OpenStandardOutput();

var handler = new LengthHeaderMessageHandler(outputStream, inputStream, new NerdbankMessagePackFormatter { TypeShapeProvider = TypeShapeProvider_SharpIDE_MsBuildHost_Contracts.Default });
var rpc = new JsonRpc(handler);

rpc.AddLocalRpcTarget<IRpcBuildService>(new RpcBuildService(), null);

rpc.StartListening();

await rpc.Completion;
