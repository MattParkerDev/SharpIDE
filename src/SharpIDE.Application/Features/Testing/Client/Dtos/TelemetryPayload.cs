using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public record TelemetryPayload
(
    [property: JsonPropertyName(nameof(TelemetryPayload.EventName))]
    string EventName,

    [property: JsonPropertyName("metrics")]
    IDictionary<string, string> Metrics);
