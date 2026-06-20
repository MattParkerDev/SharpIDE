using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record RunTestsRequest(
    [property:JsonPropertyName("runId")]
    Guid RunId,
    [property:JsonPropertyName("tests")]
    TestNode[]? Tests = null);
