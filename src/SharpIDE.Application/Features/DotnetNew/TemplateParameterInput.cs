namespace SharpIDE.Application.Features.DotnetNew;

public sealed record TemplateParameterInput(string? InitialValue, bool IsRequired = false, bool BlankUsesDefault = true)
{
	// Omission lets the engine apply defaults and generated/coalesced values.
	// In particular, null dictionary values can mean "option without a value",
	// so callers must leave the key out entirely when this returns null.
	public string? GetOverride(string? value) => (BlankUsesDefault && string.IsNullOrWhiteSpace(value))
		|| (!IsRequired && value == InitialValue) ? null : value;
}
