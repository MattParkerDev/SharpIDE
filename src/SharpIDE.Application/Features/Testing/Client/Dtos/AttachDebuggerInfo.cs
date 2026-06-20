using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record AttachDebuggerInfo(
    [property:JsonPropertyName("processId")]
    int ProcessId);
