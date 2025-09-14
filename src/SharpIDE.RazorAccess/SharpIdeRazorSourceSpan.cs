using System.Globalization;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Internal;

namespace SharpIDE.RazorAccess;

public readonly struct SharpIdeRazorSourceSpan(
	string filePath,
	int absoluteIndex,
	int lineIndex,
	int characterIndex,
	int length,
	int lineCount,
	int endCharacterIndex)
	: IEquatable<SharpIdeRazorSourceSpan>
{
	public int Length { get; } = length;
	public int AbsoluteIndex { get; } = absoluteIndex;
	public int LineIndex { get; } = lineIndex;
	public int CharacterIndex { get; } = characterIndex;
	public int LineCount { get; } = lineCount;
	public int EndCharacterIndex { get; } = endCharacterIndex;

	public string FilePath { get; } = filePath;

	public bool Equals(SharpIdeRazorSourceSpan other)
	{
		return string.Equals(FilePath, other.FilePath, StringComparison.Ordinal) && this.AbsoluteIndex == other.AbsoluteIndex && this.LineIndex == other.LineIndex && this.CharacterIndex == other.CharacterIndex && this.Length == other.Length;
	}

	public override bool Equals(object? obj) => obj is SharpIdeRazorSourceSpan other && Equals(other);

	public override int GetHashCode()
	{
		var hashCode = HashCodeCombiner.Start();
		hashCode.Add(FilePath, StringComparer.Ordinal);
		hashCode.Add(AbsoluteIndex);
		hashCode.Add(LineIndex);
		hashCode.Add(CharacterIndex);
		hashCode.Add(Length);
		return hashCode;
	}

	public override string ToString()
	{
		return string.Format(
			CultureInfo.CurrentCulture,
			"({0}:{1},{2} [{3}] {4})",
			this.AbsoluteIndex,
			this.LineIndex,
			this.CharacterIndex,
			this.Length,
			this.FilePath
		);
	}
	public static bool operator ==(SharpIdeRazorSourceSpan left, SharpIdeRazorSourceSpan right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(SharpIdeRazorSourceSpan left, SharpIdeRazorSourceSpan right)
	{
		return !(left == right);
	}
}

public static class SharpIdeRazorSourceSpanExtensions
{
	public static TextSpan AsTextSpan(this SharpIdeRazorSourceSpan sourceSpan)
	{
		return new TextSpan(sourceSpan.AbsoluteIndex, sourceSpan.Length);
	}
	public static SharpIdeRazorSourceSpan ToSharpIdeSourceSpan(this SourceSpan span)
		=> new SharpIdeRazorSourceSpan(
			span.FilePath,
			span.AbsoluteIndex,
			span.LineIndex,
			span.CharacterIndex,
			span.Length,
			span.LineCount,
			span.EndCharacterIndex);
}
