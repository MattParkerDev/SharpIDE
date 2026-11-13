using System.Collections.Frozen;
using Microsoft.CodeAnalysis.Classification;

namespace SharpIDE.Application.Features.Analysis;

/// <summary>
/// A single highlighted range in a document, for any document kind (C#, Razor, debugger expressions).
/// Always confined to a single line, and a document's spans are sorted and non-overlapping.
/// </summary>
public readonly record struct SharpIdeClassifiedSpan(
	int Line,
	int StartCharacter,
	int Length,
	SharpIdeClassification Classification,
	SharpIdeClassificationModifiers Modifiers = SharpIdeClassificationModifiers.None)
{
	public int EndCharacter => StartCharacter + Length;
}

/// <summary>
/// One vocabulary for every classification we highlight: Roslyn's <see cref="ClassificationTypeNames"/> plus Razor's semantic token types.
/// </summary>
public enum SharpIdeClassification : byte
{
	Unknown,
	Text,
	WhiteSpace,

	// C#
	Comment,
	ExcludedCode,
	Identifier,
	Keyword,
	ControlKeyword,
	NumericLiteral,
	Operator,
	OperatorOverloaded,
	PreprocessorKeyword,
	PreprocessorText,
	StringLiteral,
	VerbatimStringLiteral,
	StringEscapeCharacter,
	Punctuation,

	ClassName,
	RecordClassName,
	DelegateName,
	EnumName,
	InterfaceName,
	ModuleName,
	StructName,
	RecordStructName,
	TypeParameterName,

	FieldName,
	EnumMemberName,
	ConstantName,
	LocalName,
	ParameterName,
	MethodName,
	ExtensionMethodName,
	PropertyName,
	EventName,
	NamespaceName,
	LabelName,

	XmlDocCommentAttributeName,
	XmlDocCommentAttributeQuotes,
	XmlDocCommentAttributeValue,
	XmlDocCommentCDataSection,
	XmlDocCommentComment,
	XmlDocCommentDelimiter,
	XmlDocCommentEntityReference,
	XmlDocCommentName,
	XmlDocCommentProcessingInstruction,
	XmlDocCommentText,

	RegexComment,
	RegexCharacterClass,
	RegexAnchor,
	RegexQuantifier,
	RegexGrouping,
	RegexAlternation,
	RegexText,
	RegexSelfEscapedCharacter,
	RegexOtherEscape,

	JsonComment,
	JsonNumber,
	JsonString,
	JsonKeyword,
	JsonText,
	JsonOperator,
	JsonPunctuation,
	JsonArray,
	JsonObject,
	JsonPropertyName,
	JsonConstructorName,

	// Razor / markup
	MarkupAttribute,
	MarkupAttributeQuote,
	MarkupAttributeValue,
	MarkupComment,
	MarkupCommentPunctuation,
	MarkupElement,
	MarkupOperator,
	MarkupTagDelimiter,
	MarkupTextLiteral,
	RazorComment,
	RazorCommentStar,
	RazorCommentTransition,
	RazorComponentAttribute,
	RazorComponentElement,
	RazorDirective,
	RazorDirectiveAttribute,
	RazorDirectiveColon,
	RazorTagHelperAttribute,
	RazorTagHelperElement,
	RazorTransition,
}

/// <summary>Roslyn's 'additive' classifications, which decorate a span's main classification rather than replace it.</summary>
[Flags]
public enum SharpIdeClassificationModifiers : byte
{
	None = 0,
	Static = 1 << 0,
	ReassignedVariable = 1 << 1,
	Obsolete = 1 << 2,
}

public static class SharpIdeClassifications
{
	public static readonly int Count = Enum.GetValues<SharpIdeClassification>().Length;

	private static readonly FrozenDictionary<string, SharpIdeClassificationModifiers> _modifiersByName = new Dictionary<string, SharpIdeClassificationModifiers>
	{
		[ClassificationTypeNames.StaticSymbol] = SharpIdeClassificationModifiers.Static,
		[ClassificationTypeNames.ReassignedVariable] = SharpIdeClassificationModifiers.ReassignedVariable,
		[ClassificationTypeNames.ObsoleteSymbol] = SharpIdeClassificationModifiers.Obsolete,
		[ClassificationTypeNames.TestCode] = SharpIdeClassificationModifiers.None,
	}.ToFrozenDictionary(StringComparer.Ordinal);

	private static readonly FrozenDictionary<string, SharpIdeClassification> _classificationsByName = new Dictionary<string, SharpIdeClassification>
	{
		[ClassificationTypeNames.Text] = SharpIdeClassification.Text,
		[ClassificationTypeNames.WhiteSpace] = SharpIdeClassification.WhiteSpace,

		[ClassificationTypeNames.Comment] = SharpIdeClassification.Comment,
		[ClassificationTypeNames.ExcludedCode] = SharpIdeClassification.ExcludedCode,
		[ClassificationTypeNames.Identifier] = SharpIdeClassification.Identifier,
		[ClassificationTypeNames.Keyword] = SharpIdeClassification.Keyword,
		[ClassificationTypeNames.ControlKeyword] = SharpIdeClassification.ControlKeyword,
		[ClassificationTypeNames.NumericLiteral] = SharpIdeClassification.NumericLiteral,
		[ClassificationTypeNames.Operator] = SharpIdeClassification.Operator,
		[ClassificationTypeNames.OperatorOverloaded] = SharpIdeClassification.OperatorOverloaded,
		[ClassificationTypeNames.PreprocessorKeyword] = SharpIdeClassification.PreprocessorKeyword,
		[ClassificationTypeNames.PreprocessorText] = SharpIdeClassification.PreprocessorText,
		[ClassificationTypeNames.StringLiteral] = SharpIdeClassification.StringLiteral,
		[ClassificationTypeNames.VerbatimStringLiteral] = SharpIdeClassification.VerbatimStringLiteral,
		[ClassificationTypeNames.StringEscapeCharacter] = SharpIdeClassification.StringEscapeCharacter,
		[ClassificationTypeNames.Punctuation] = SharpIdeClassification.Punctuation,

		[ClassificationTypeNames.ClassName] = SharpIdeClassification.ClassName,
		[ClassificationTypeNames.RecordClassName] = SharpIdeClassification.RecordClassName,
		[ClassificationTypeNames.DelegateName] = SharpIdeClassification.DelegateName,
		[ClassificationTypeNames.EnumName] = SharpIdeClassification.EnumName,
		[ClassificationTypeNames.InterfaceName] = SharpIdeClassification.InterfaceName,
		[ClassificationTypeNames.ModuleName] = SharpIdeClassification.ModuleName,
		[ClassificationTypeNames.StructName] = SharpIdeClassification.StructName,
		[ClassificationTypeNames.RecordStructName] = SharpIdeClassification.RecordStructName,
		[ClassificationTypeNames.TypeParameterName] = SharpIdeClassification.TypeParameterName,

		[ClassificationTypeNames.FieldName] = SharpIdeClassification.FieldName,
		[ClassificationTypeNames.EnumMemberName] = SharpIdeClassification.EnumMemberName,
		[ClassificationTypeNames.ConstantName] = SharpIdeClassification.ConstantName,
		[ClassificationTypeNames.LocalName] = SharpIdeClassification.LocalName,
		[ClassificationTypeNames.ParameterName] = SharpIdeClassification.ParameterName,
		[ClassificationTypeNames.MethodName] = SharpIdeClassification.MethodName,
		[ClassificationTypeNames.ExtensionMethodName] = SharpIdeClassification.ExtensionMethodName,
		[ClassificationTypeNames.PropertyName] = SharpIdeClassification.PropertyName,
		[ClassificationTypeNames.EventName] = SharpIdeClassification.EventName,
		[ClassificationTypeNames.NamespaceName] = SharpIdeClassification.NamespaceName,
		[ClassificationTypeNames.LabelName] = SharpIdeClassification.LabelName,

		[ClassificationTypeNames.XmlDocCommentAttributeName] = SharpIdeClassification.XmlDocCommentAttributeName,
		[ClassificationTypeNames.XmlDocCommentAttributeQuotes] = SharpIdeClassification.XmlDocCommentAttributeQuotes,
		[ClassificationTypeNames.XmlDocCommentAttributeValue] = SharpIdeClassification.XmlDocCommentAttributeValue,
		[ClassificationTypeNames.XmlDocCommentCDataSection] = SharpIdeClassification.XmlDocCommentCDataSection,
		[ClassificationTypeNames.XmlDocCommentComment] = SharpIdeClassification.XmlDocCommentComment,
		[ClassificationTypeNames.XmlDocCommentDelimiter] = SharpIdeClassification.XmlDocCommentDelimiter,
		[ClassificationTypeNames.XmlDocCommentEntityReference] = SharpIdeClassification.XmlDocCommentEntityReference,
		[ClassificationTypeNames.XmlDocCommentName] = SharpIdeClassification.XmlDocCommentName,
		[ClassificationTypeNames.XmlDocCommentProcessingInstruction] = SharpIdeClassification.XmlDocCommentProcessingInstruction,
		[ClassificationTypeNames.XmlDocCommentText] = SharpIdeClassification.XmlDocCommentText,

		[ClassificationTypeNames.RegexComment] = SharpIdeClassification.RegexComment,
		[ClassificationTypeNames.RegexCharacterClass] = SharpIdeClassification.RegexCharacterClass,
		[ClassificationTypeNames.RegexAnchor] = SharpIdeClassification.RegexAnchor,
		[ClassificationTypeNames.RegexQuantifier] = SharpIdeClassification.RegexQuantifier,
		[ClassificationTypeNames.RegexGrouping] = SharpIdeClassification.RegexGrouping,
		[ClassificationTypeNames.RegexAlternation] = SharpIdeClassification.RegexAlternation,
		[ClassificationTypeNames.RegexText] = SharpIdeClassification.RegexText,
		[ClassificationTypeNames.RegexSelfEscapedCharacter] = SharpIdeClassification.RegexSelfEscapedCharacter,
		[ClassificationTypeNames.RegexOtherEscape] = SharpIdeClassification.RegexOtherEscape,

		[ClassificationTypeNames.JsonComment] = SharpIdeClassification.JsonComment,
		[ClassificationTypeNames.JsonNumber] = SharpIdeClassification.JsonNumber,
		[ClassificationTypeNames.JsonString] = SharpIdeClassification.JsonString,
		[ClassificationTypeNames.JsonKeyword] = SharpIdeClassification.JsonKeyword,
		[ClassificationTypeNames.JsonText] = SharpIdeClassification.JsonText,
		[ClassificationTypeNames.JsonOperator] = SharpIdeClassification.JsonOperator,
		[ClassificationTypeNames.JsonPunctuation] = SharpIdeClassification.JsonPunctuation,
		[ClassificationTypeNames.JsonArray] = SharpIdeClassification.JsonArray,
		[ClassificationTypeNames.JsonObject] = SharpIdeClassification.JsonObject,
		[ClassificationTypeNames.JsonPropertyName] = SharpIdeClassification.JsonPropertyName,
		[ClassificationTypeNames.JsonConstructorName] = SharpIdeClassification.JsonConstructorName,

		// Razor semantic token types, see Microsoft.CodeAnalysis.Razor.SemanticTokens.SemanticTokenTypes
		["markupAttribute"] = SharpIdeClassification.MarkupAttribute,
		["markupAttributeQuote"] = SharpIdeClassification.MarkupAttributeQuote,
		["markupAttributeValue"] = SharpIdeClassification.MarkupAttributeValue,
		["markupComment"] = SharpIdeClassification.MarkupComment,
		["markupCommentPunctuation"] = SharpIdeClassification.MarkupCommentPunctuation,
		["markupElement"] = SharpIdeClassification.MarkupElement,
		["markupOperator"] = SharpIdeClassification.MarkupOperator,
		["markupTagDelimiter"] = SharpIdeClassification.MarkupTagDelimiter,
		["markupTextLiteral"] = SharpIdeClassification.MarkupTextLiteral,
		["razorComment"] = SharpIdeClassification.RazorComment,
		["razorCommentStar"] = SharpIdeClassification.RazorCommentStar,
		["razorCommentTransition"] = SharpIdeClassification.RazorCommentTransition,
		["razorComponentAttribute"] = SharpIdeClassification.RazorComponentAttribute,
		["razorComponentElement"] = SharpIdeClassification.RazorComponentElement,
		["razorDirective"] = SharpIdeClassification.RazorDirective,
		["razorDirectiveAttribute"] = SharpIdeClassification.RazorDirectiveAttribute,
		["razorDirectiveColon"] = SharpIdeClassification.RazorDirectiveColon,
		["razorTagHelperAttribute"] = SharpIdeClassification.RazorTagHelperAttribute,
		["razorTagHelperElement"] = SharpIdeClassification.RazorTagHelperElement,
		["razorTransition"] = SharpIdeClassification.RazorTransition,
	}.ToFrozenDictionary(StringComparer.Ordinal);

	public static SharpIdeClassification FromName(string classificationTypeName)
		=> _classificationsByName.GetValueOrDefault(classificationTypeName, SharpIdeClassification.Unknown);

	public static bool TryGetModifier(string classificationTypeName, out SharpIdeClassificationModifiers modifier)
		=> _modifiersByName.TryGetValue(classificationTypeName, out modifier);
}
