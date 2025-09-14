using System.Globalization;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.Extensions.Internal;

namespace SharpIDE.RazorAccess;

public sealed class SharpIdeRazorSourceMapping(
	SharpIdeRazorSourceSpan originalSpan,
	SharpIdeRazorSourceSpan generatedSpan)
	: IEquatable<SharpIdeRazorSourceMapping>
{
	public SharpIdeRazorSourceSpan OriginalSpan { get; } = originalSpan;

	public SharpIdeRazorSourceSpan GeneratedSpan { get; } = generatedSpan;

	public override bool Equals(object? obj) => Equals(obj as SourceMapping);

	public bool Equals(SharpIdeRazorSourceMapping? other)
	{
		if (other == null)
			return false;
		var sourceSpan = OriginalSpan;
		if (!sourceSpan.Equals(other.OriginalSpan))
			return false;
		sourceSpan = GeneratedSpan;
		return sourceSpan.Equals(other.GeneratedSpan);
	}

	public override int GetHashCode()
	{
		HashCodeCombiner hashCode = HashCodeCombiner.Start();
		hashCode.Add(OriginalSpan);
		hashCode.Add(GeneratedSpan);
		return hashCode;
	}

	public override string ToString()
	{
		return string.Format(CultureInfo.CurrentCulture, "{0} -> {1}", OriginalSpan, GeneratedSpan);
	}
}

public static class SharpIdeRazorSourceMappingExtensions
{
	public static SharpIdeRazorSourceMapping ToSharpIdeSourceMapping(this SourceMapping mapping)
	{
		return new SharpIdeRazorSourceMapping(mapping.OriginalSpan.ToSharpIdeSourceSpan(), mapping.GeneratedSpan.ToSharpIdeSourceSpan());
	}
}
