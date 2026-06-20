using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record ServerInfo(
    [property:JsonPropertyName("name")]
    string Name,

    [property:JsonPropertyName("version")]
    string Version = "1.0.0");
