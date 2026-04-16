using PolyType;
using StreamJsonRpc;

namespace SharpIDE.MsBuildHost.Contracts;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IRpcBuildService
{
	Task<bool> Ping();
	Task<(BuildResultDto buildResultDto, Exception? Exception)> MsBuildAsync(string solutionOrProjectFilePath, BuildTypeDto buildType = BuildTypeDto.Build, CancellationToken cancellationToken = default);
}
