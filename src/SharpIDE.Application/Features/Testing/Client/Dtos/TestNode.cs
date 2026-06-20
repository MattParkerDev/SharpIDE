using System.Text.Json;
using System.Text.Json.Serialization;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record TestNode
{
    [JsonPropertyName("uid")]
    public required string Uid { get; init; }

    [JsonPropertyName("display-name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("node-type")]
    public string? NodeType { get; init; }

    [JsonPropertyName("execution-state")]
    public string? ExecutionState { get; init; }

    [JsonPropertyName("location.type")] // Containing Class
	public required string LocationType { get; init; }

	[JsonPropertyName("location.method")]
	public required string LocationMethod { get; init; }

    // Captures every other server-sent field (traits, location.*, error.*, etc.)
    // so that when we round-trip the node back in testCases, the server sees
    // the full payload it originally produced.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    // Not serialized or returned by MTP - added by us
    [JsonIgnore]
    public SharpIdeProjectModel? Project;
}
