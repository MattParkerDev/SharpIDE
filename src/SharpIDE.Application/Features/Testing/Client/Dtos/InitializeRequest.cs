using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record InitializeRequest(
    [property:JsonPropertyName("processId")]
    int ProcessId,

    [property:JsonPropertyName("clientInfo")]
    ClientInfo ClientInfo,

    [property:JsonPropertyName("capabilities")]
    ClientCapabilities Capabilities);
