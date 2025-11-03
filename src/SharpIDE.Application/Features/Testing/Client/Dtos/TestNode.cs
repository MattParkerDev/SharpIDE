using Newtonsoft.Json;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record TestNodeUpdate
(
	[property: JsonProperty("node")]
	TestNode Node,

	[property: JsonProperty("parent")]
	string ParentUid);

// https://github.com/microsoft/testfx/blob/main/docs/mstest-runner-protocol/003-protocol-ide-integration-extensions.md
public sealed record TestNode
(
	[property: JsonProperty("uid")]
	string Uid,

	[property: JsonProperty("display-name")]
	string DisplayName,

	[property: JsonProperty("node-type")]
	string NodeType,

	[property: JsonProperty("execution-state")]
	string ExecutionState,

	[property: JsonProperty("location.file")]
	string LocationFile,

	[property: JsonProperty("location.type")] // Containing Class
	string LocationType,

	[property: JsonProperty("location.method")]
	string LocationMethod,

	[property: JsonProperty("location.line-start")]
	int LocationLineStart,

	[property: JsonProperty("location.line-end")]
	int LocationLineEnd);

