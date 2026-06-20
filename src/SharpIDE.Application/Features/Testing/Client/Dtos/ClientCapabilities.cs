using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record ClientCapabilities(
    [property: JsonPropertyName("testing")]
    ClientTestingCapabilities Testing);
