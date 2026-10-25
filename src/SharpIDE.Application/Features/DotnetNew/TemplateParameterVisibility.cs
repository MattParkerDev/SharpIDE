using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Edge.Settings;

namespace SharpIDE.Application.Features.DotnetNew;

public sealed class TemplateParameterVisibility
{
	private readonly Dictionary<string, bool> _visibility = new(StringComparer.OrdinalIgnoreCase);
	private readonly bool _defaultVisibility;

	private TemplateParameterVisibility(bool defaultVisibility) => _defaultVisibility = defaultVisibility;

	public bool IsVisible(string parameterName) => _visibility.GetValueOrDefault(parameterName, _defaultVisibility);

	public static TemplateParameterVisibility Load(ITemplateInfo template, ILogger logger)
	{
		// The engine selects and caches the host file using our fallback order.
		// Its content is intentionally untyped: presentation belongs to the host.
		if (template is not ITemplateInfoHostJsonCache { HostData: { } json }) return new(true);
		try
		{
			using var document = JsonDocument.Parse(json);
			var isCli = string.Equals(Path.GetFileName(template.HostConfigPlace), "dotnetcli.host.json", StringComparison.OrdinalIgnoreCase);
			return Parse(document.RootElement, isIde: !isCli);
		}
		catch (JsonException ex)
		{
			logger.LogWarning(ex, "Could not interpret cached host metadata {HostFile} for template {Identity}", template.HostConfigPlace, template.Identity);
		}

		return new TemplateParameterVisibility(true);
	}

	private static TemplateParameterVisibility Parse(JsonElement root, bool isIde)
	{
		if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Host configuration must be an object.");
		var defaultVisibility = !isIde || ReadBoolean(root, "defaultSymbolVisibility", false);
		var result = new TemplateParameterVisibility(defaultVisibility);
		if (!root.TryGetProperty("symbolInfo", out var symbols)) return result;

		if (isIde)
		{
			if (symbols.ValueKind != JsonValueKind.Array) throw new JsonException("IDE symbolInfo must be an array.");
			foreach (var symbol in symbols.EnumerateArray())
			{
				if (symbol.ValueKind != JsonValueKind.Object || !symbol.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
					throw new JsonException("IDE symbols must have a string id.");
				result._visibility[id.GetString()!] = ReadBoolean(symbol, "isVisible", defaultVisibility);
			}
		}
		else
		{
			if (symbols.ValueKind != JsonValueKind.Object) throw new JsonException("CLI symbolInfo must be an object.");
			foreach (var symbol in symbols.EnumerateObject())
			{
				result._visibility[symbol.Name] = !ReadBoolean(symbol.Value, "isHidden", false);
			}
		}
		return result;
	}

	private static bool ReadBoolean(JsonElement element, string name, bool defaultValue)
	{
		if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Symbol metadata must be an object.");
		if (!element.TryGetProperty(name, out var value)) return defaultValue;
		return value.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			// Older SDK templates also encode these flags as "true" / "false".
			JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
			_ => throw new JsonException($"{name} must be a boolean.")
		};
	}
}
