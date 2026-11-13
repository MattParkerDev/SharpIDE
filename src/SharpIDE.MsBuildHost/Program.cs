using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using PolyType.SourceGenerator;
using SharpIDE.MsBuildHost;
using SharpIDE.MsBuildHost.Contracts;
using StreamJsonRpc;

var sdkVersion = args[0];
Guard.Against.NullOrWhiteSpace(sdkVersion);
var projectOrSlnDirectory = new DirectoryInfo(args[1]);
if (projectOrSlnDirectory.Exists is false) throw new DirectoryNotFoundException($"Project or sln directory '{projectOrSlnDirectory.FullName}' does not exist.");

SharpIdeMsbuildLocator.Register(sdkVersion, projectOrSlnDirectory.FullName);

if (args.Contains("--diag"))
{
	Console.WriteLine($"'{RuntimeInformation.FrameworkDescription}' Runtime, MSBuild from SDK: '{SharpIdeMsbuildLocator.ResolvedMsBuildSdkPath}'");
}

var inputStream = Console.OpenStandardInput();
var outputStream = Console.OpenStandardOutput();

var handler = new LengthHeaderMessageHandler(outputStream, inputStream, new NerdbankMessagePackFormatter { TypeShapeProvider = TypeShapeProvider_SharpIDE_MsBuildHost_Contracts.Default });
var rpc = new JsonRpc(handler);

// Two hosts per solution: one for the user's builds (and evaluation), one (--design-time) for the design-time builds the IDE's Roslyn workspace is loaded from.
// They are separate processes as MSBuild runs one build session per process, and a session's submissions can only be cancelled all together.
if (args.Contains("--design-time"))
{
	rpc.AddLocalRpcTarget<IRpcDesignTimeBuildService>(new RpcDesignTimeBuildService(), null);
}
else
{
	rpc.AddLocalRpcTarget<IRpcBuildService>(new RpcBuildService(), null);
}

rpc.StartListening();

await rpc.Completion;
