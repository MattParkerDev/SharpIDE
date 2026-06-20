using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public record TestNodeStateChangedEventArgs(
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("changes")] TestNodeUpdate[] Changes
    );
