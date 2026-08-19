using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.CodeEditor;

public sealed partial class CodeCompletionPopup : IDisposable
{
	private static readonly string[] CompletionTriggers =
	[
		"a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
		"_", "<", ".", "#",
	];

	private readonly CodeEdit _codeEdit;
	private readonly Rid _canvasItemRid;
	private readonly Window _descriptionWindow;
	private readonly RichTextLabel _descriptionLabel;
	private readonly CustomHighlighter _syntaxHighlighter;
	private readonly Func<CompletionItem, CancellationToken, Task<CompletionDescription>> _getDescriptionAsync;
	private readonly Action<CompletionTrigger> _requestCompletion;
	private readonly Action<CompletionItem> _applyCompletion;
	private readonly bool _preferAbove;
	private readonly Texture2D _csharpMethodIcon = ResourceLoader.Load<Texture2D>("uid://b17p18ijhvsep");
	private readonly Texture2D _csharpClassIcon = ResourceLoader.Load<Texture2D>("uid://b027uufaewitj");
	private readonly Texture2D _csharpInterfaceIcon = ResourceLoader.Load<Texture2D>("uid://bdwmkdweqvowt");
	private readonly Texture2D _localVariableIcon = ResourceLoader.Load<Texture2D>("uid://vwvkxlnvqqk3");
	private readonly Texture2D _fieldIcon = ResourceLoader.Load<Texture2D>("uid://c4y7d5m4upfju");
	private readonly Texture2D _parameterIcon = ResourceLoader.Load<Texture2D>("uid://b0bv71yfmd08f");
	private readonly Texture2D _propertyIcon = ResourceLoader.Load<Texture2D>("uid://y5pwrwwrjqmc");
	private readonly Texture2D _keywordIcon = ResourceLoader.Load<Texture2D>("uid://b0ujhoq2xg2v0");
	private readonly Texture2D _namespaceIcon = ResourceLoader.Load<Texture2D>("uid://bob5blfjll4h3");
	private readonly Texture2D _eventIcon = ResourceLoader.Load<Texture2D>("uid://c3upo3lxmgtls");
	private readonly Texture2D _enumIcon = ResourceLoader.Load<Texture2D>("uid://8mdxo65qepqv");
	private readonly Texture2D _delegateIcon = ResourceLoader.Load<Texture2D>("uid://c83pv25rdescy");
	private ImmutableArray<SharpIdeCompletionItem> _options = [];
	private CancellationTokenSource? _descriptionCancellationTokenSource;
	private CompletionTrigger? _pendingCompletionTrigger;
	private CompletionFilterReason? _pendingFilterReason;
	private Vector2I? _triggerPosition;
	private int _lineOffset;
	private int _forceItemCenter = -1;
	private int _selectedIndex;
	private int _minLineWidth;
	private bool _disposed;

	public CodeCompletionPopup(
		CodeEdit codeEdit,
		Rid canvasItemRid,
		Window descriptionWindow,
		RichTextLabel descriptionLabel,
		CustomHighlighter syntaxHighlighter,
		Func<CompletionItem, CancellationToken, Task<CompletionDescription>> getDescriptionAsync,
		Action<CompletionTrigger> requestCompletion,
		Action<CompletionItem> applyCompletion,
		bool preferAbove = false)
	{
		_codeEdit = codeEdit;
		_canvasItemRid = canvasItemRid;
		_descriptionWindow = descriptionWindow;
		_descriptionLabel = descriptionLabel;
		_syntaxHighlighter = syntaxHighlighter;
		_getDescriptionAsync = getDescriptionAsync;
		_requestCompletion = requestCompletion;
		_applyCompletion = applyCompletion;
		_preferAbove = preferAbove;
	}

	public bool IsOpen => _options.IsDefaultOrEmpty is false;

	public void SetTriggerPosition(Vector2I triggerPosition) => _triggerPosition = triggerPosition;

	public void SetOptions(ImmutableArray<SharpIdeCompletionItem> options)
	{
		if (options.IsDefaultOrEmpty)
		{
			Reset();
			return;
		}

		var previouslySelectedItem = IsOpen ? _options[_selectedIndex].CompletionItem : null;
		_options = options;
		var newSelectedIndex = 0;
		if (previouslySelectedItem is not null)
		{
			for (var index = 0; index < options.Length; index++)
			{
				if (options[index].CompletionItem == previouslySelectedItem)
				{
					newSelectedIndex = index;
					break;
				}
			}
		}
		SetSelectedCompletion(Math.Max(0, newSelectedIndex));
		_codeEdit.QueueRedraw();
	}

	public CompletionTrigger? TakePendingCompletionTrigger()
	{
		var trigger = _pendingCompletionTrigger;
		_pendingCompletionTrigger = null;
		return trigger;
	}

	public CompletionFilterReason? TakePendingFilterReason()
	{
		var reason = _pendingFilterReason;
		_pendingFilterReason = null;
		return reason;
	}

	public void Reset()
	{
		_options = [];
		_triggerPosition = null;
		_selectedIndex = 0;
		_lineOffset = 0;
		_minLineWidth = 0;
		_forceItemCenter = -1;
		_pendingCompletionTrigger = null;
		_pendingFilterReason = null;
		CancelDescriptionRequest();
		_descriptionWindow.Hide();
		_descriptionLabel.Clear();
		_codeEdit.QueueRedraw();
	}

	public bool TryConsumeGuiInput(InputEvent @event)
	{
		if (IsOpen is false && @event.IsActionPressed(InputStringNames.CodeEditorRequestCompletions))
		{
			_requestCompletion(new CompletionTrigger(CompletionTriggerKind.InvokeAndCommitIfUnique));
			return true;
		}

		if (IsOpen)
		{
			if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } mouseEvent)
			{
				var optionIndex = GetOptionAtPoint((Vector2I)mouseEvent.Position);
				if (optionIndex is null)
				{
					Reset();
					return false;
				}

				if (_forceItemCenter is -1)
				{
					_forceItemCenter = _selectedIndex;
				}

				SetSelectedCompletion(optionIndex.Value);
				if (mouseEvent.DoubleClick)
				{
					ApplySelectedCompletion();
				}
				else
				{
					_codeEdit.QueueRedraw();
				}
				return true;
			}

			if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown or MouseButton.WheelUp } scrollEvent &&
				_completionRect.HasPoint((Vector2I)scrollEvent.Position))
			{
				var scrollAmount = scrollEvent.ButtonIndex is MouseButton.WheelDown ? 1 : -1;
				_forceItemCenter = -1;
				SetSelectedCompletion(Mathf.Clamp(_selectedIndex + scrollAmount, 0, _options.Length - 1));
				_codeEdit.QueueRedraw();
				return true;
			}

			if (@event.IsActionPressed(InputStringNames.Backspace))
			{
				_pendingFilterReason = CompletionFilterReason.Deletion;
				return false;
			}

			if (@event is InputEventKey { Pressed: true, Keycode: Key.Up or Key.Down } keyEvent)
			{
				var delta = keyEvent.Keycode is Key.Up ? -1 : 1;
				_forceItemCenter = -1;
				SetSelectedCompletion(Mathf.Clamp(_selectedIndex + delta, 0, _options.Length - 1));
				_codeEdit.QueueRedraw();
				return true;
			}

			if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
			{
				Reset();
				return true;
			}

			if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.Tab })
			{
				ApplySelectedCompletion();
				return true;
			}
		}

		if (@event is not InputEventKey { Pressed: true, Unicode: not 0 } insertionEvent)
		{
			return false;
		}

		var insertedText = char.ConvertFromUtf32((int)insertionEvent.Unicode);
		if (IsOpen)
		{
			if (insertedText is " ")
			{
				Reset();
				return false;
			}

			if (insertedText is ".")
			{
				Reset();
				_pendingCompletionTrigger = CompletionTrigger.CreateInsertionTrigger('.');
				return false;
			}

			if (insertionEvent.Unicode > 32)
			{
				_pendingFilterReason = CompletionFilterReason.Insertion;
				return false;
			}
		}

		if (IsOpen is false && CompletionTriggers.Contains(insertedText, StringComparer.OrdinalIgnoreCase))
		{
			_pendingCompletionTrigger = CompletionTrigger.CreateInsertionTrigger(insertedText[0]);
		}

		return false;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		CancelDescriptionRequest();
	}

	private void ApplySelectedCompletion()
	{
		var completionItem = _options[_selectedIndex].CompletionItem;
		Reset();
		_applyCompletion(completionItem);
	}

	private void SetSelectedCompletion(int index)
	{
		_selectedIndex = index;
		var completionItem = _options[index].CompletionItem;
		CancelDescriptionRequest();
		_descriptionCancellationTokenSource = new CancellationTokenSource();
		var cancellationToken = _descriptionCancellationTokenSource.Token;
		_ = Task.GodotRun(async () =>
		{
			try
			{
				var description = await _getDescriptionAsync(completionItem, cancellationToken);
				await _codeEdit.InvokeAsync(() =>
				{
					if (cancellationToken.IsCancellationRequested || IsOpen is false || _options[_selectedIndex].CompletionItem != completionItem)
					{
						return;
					}

					_descriptionLabel.Clear();
					_descriptionWindow.Size = new Vector2I(10, 10);
					CompletionDescriptionTooltip.WriteToCompletionDescriptionLabel(_descriptionLabel, description, _syntaxHighlighter.ColourSetForTheme);
					_descriptionWindow.Show();
				});
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
			{
			}
		});
	}

	private int? GetOptionAtPoint(Vector2I point)
	{
		if (_completionRect.HasPoint(point) is false)
		{
			return null;
		}

		var lineIndex = (point.Y - _completionRect.Position.Y) / _codeEdit.GetLineHeight() + _lineOffset;
		return lineIndex >= 0 && lineIndex < _options.Length ? lineIndex : null;
	}

	private Texture2D? GetIcon(CompletionItem completionItem)
	{
		var symbolKindValue = completionItem.Properties.GetValueOrDefault("SymbolKind");
		var symbolKind = symbolKindValue is null ? null : (SymbolKind?)int.Parse(symbolKindValue);
		var typeKind = Enum.TryParse<TypeKind>(completionItem.Tags.ElementAtOrDefault(0), out var parsedTypeKind) ? parsedTypeKind : (TypeKind?)null;
		var accessibility = Enum.TryParse<Accessibility>(completionItem.Tags.Skip(1).FirstOrDefault(), out var parsedAccessibility) ? parsedAccessibility : (Accessibility?)null;
		var isKeyword = completionItem.Tags.Contains(WellKnownTags.Keyword);
		if (symbolKind is null && (completionItem.Tags.Contains(WellKnownTags.Method) || completionItem.Tags.Contains(WellKnownTags.ExtensionMethod)))
		{
			symbolKind = SymbolKind.Method;
		}

		return (symbolKind, typeKind, accessibility, isKeyword) switch
		{
			(_, _, _, true) => _keywordIcon,
			(SymbolKind.Method, _, _, _) => _csharpMethodIcon,
			(_, TypeKind.Interface, _, _) => _csharpInterfaceIcon,
			(_, TypeKind.Enum, _, _) => _enumIcon,
			(_, TypeKind.Delegate, _, _) => _delegateIcon,
			(_, TypeKind.Class or TypeKind.Struct, _, _) => _csharpClassIcon,
			(SymbolKind.NamedType, _, _, _) => _csharpClassIcon,
			(SymbolKind.Local, _, _, _) => _localVariableIcon,
			(SymbolKind.Field, _, _, _) => _fieldIcon,
			(SymbolKind.Parameter, _, _, _) => _parameterIcon,
			(SymbolKind.Property, _, _, _) => _propertyIcon,
			(SymbolKind.Namespace, _, _, _) => _namespaceIcon,
			(SymbolKind.Event, _, _, _) => _eventIcon,
			_ => null,
		};
	}

	private void CancelDescriptionRequest()
	{
		_descriptionCancellationTokenSource?.Cancel();
		_descriptionCancellationTokenSource?.Dispose();
		_descriptionCancellationTokenSource = null;
	}
}
