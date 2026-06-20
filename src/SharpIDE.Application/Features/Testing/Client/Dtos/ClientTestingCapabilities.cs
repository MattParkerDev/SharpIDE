using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record ClientTestingCapabilities(
    [property: JsonPropertyName("debuggerProvider")]
    bool DebuggerProvider);
