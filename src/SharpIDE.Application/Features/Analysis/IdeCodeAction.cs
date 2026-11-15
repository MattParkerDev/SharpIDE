using Microsoft.CodeAnalysis.CodeActions;

namespace SharpIDE.Application.Features.Analysis;

/// Mirrors the LSP CodeActionKinds that the Roslyn LSP reports (quickfix, refactor.extract, refactor.inline, refactor)
public enum IdeCodeActionKind
{
	QuickFix,
	RefactorExtract,
	RefactorInline,
	Refactor
}

/// A code action, and its kind. Nested actions share the kind of their parent, as in the Roslyn LSP
/// IsErrorFix is whether Roslyn categorised the fix as fixing an error (ErrorFix), rather than a warning, info or hidden diagnostic (CodeFix)
public readonly record struct IdeCodeAction(CodeAction CodeAction, IdeCodeActionKind Kind, bool IsErrorFix);
