using System.Collections.Immutable;
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

/// An entry in the code actions menu.
/// - CodeAction and no Children: applies the code action
/// - Children and no CodeAction: a container for nested actions (e.g. "Configure or suppress issues"), which cannot be applied itself
/// - CodeAction and Children: applies the code action, and its children fix all occurrences of it, in each scope (e.g. "in method 'Foo'", "in Solution")
public sealed record IdeCodeActionItem(string Title, CodeAction? CodeAction, ImmutableArray<IdeCodeActionItem> Children);

/// A top level code action, and its kind. Nested actions share the kind of their parent, as in the Roslyn LSP
/// IsErrorFix is whether Roslyn categorised the fix as fixing an error (ErrorFix), rather than a warning, info or hidden diagnostic (CodeFix)
public readonly record struct IdeCodeAction(IdeCodeActionItem Item, IdeCodeActionKind Kind, bool IsErrorFix);
