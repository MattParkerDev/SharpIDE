using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record ServerCapabilities(
    [property: JsonPropertyName("testing")]
    ServerTestingCapabilities Testing);
