using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record DiscoveryRequest(
    [property:JsonPropertyName("runId")]
    Guid RunId);
