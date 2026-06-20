using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpIDE.Application.Features.Testing.Client;

public static class RpcJsonSerializerOptions
{
	public static JsonSerializerOptions Default { get; } = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		Converters =
		{
			new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true),
		},
	};
}
