using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record TestNodeUpdate
(
    [property: JsonPropertyName("node")]
    TestNode Node,

    [property: JsonPropertyName("parent")]
    string? ParentUid);
