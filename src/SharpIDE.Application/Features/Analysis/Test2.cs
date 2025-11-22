using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace SharpIDE.Application.Features.Analysis;

public sealed class SharpIdeSourceTextContainer(ITextBuffer textBuffer) : SourceTextContainer
{
	public ITextBuffer TextBuffer { get; } = textBuffer;
	public override SourceText CurrentText => TextBuffer.CurrentSnapshot.AsText();
	private readonly Lock lockObj = new Lock();
	private EventHandler<TextChangeEventArgs>? realTextChangedEvent;

	public override event EventHandler<TextChangeEventArgs>? TextChanged {
		add {
			lock (lockObj) {
				if (realTextChangedEvent is null)
					TextBuffer.Changed += TextBuffer_Changed;
				realTextChangedEvent += value;
			}
		}
		remove {
			lock (lockObj) {
				realTextChangedEvent -= value;
				if (realTextChangedEvent is null)
					TextBuffer.Changed -= TextBuffer_Changed;
			}
		}
	}

	public void TextBuffer_Changed(object? sender, TextContentChangedEventArgs e) => realTextChangedEvent?.Invoke(this, e.ToTextChangeEventArgs());
}

public interface ITextBuffer
{
	public ITextSnapshot CurrentSnapshot { get; }
	public event EventHandler<TextContentChangedEventArgs> Changed;
}

public interface ITextSnapshot
{
	public SourceText AsText();
}

public class TextContentChangedEventArgs : EventArgs
{
	public ITextSnapshot OldSnapshot { get; }
	public ITextSnapshot NewSnapshot { get; }
	public IReadOnlyList<TextChange> Changes { get; }

	public TextContentChangedEventArgs(ITextSnapshot oldSnapshot, ITextSnapshot newSnapshot, IReadOnlyList<TextChange> changes)
	{
		OldSnapshot = oldSnapshot;
		NewSnapshot = newSnapshot;
		Changes = changes;
	}

	public TextChangeEventArgs ToTextChangeEventArgs()
	{
		var oldText = OldSnapshot.AsText();
		var newText = NewSnapshot.AsText();

		var textChangeRanges = Changes.Select(s => s.ToTextChangeRange());
		return new TextChangeEventArgs(oldText, newText, textChangeRanges);
	}
}

public class SharpIdeTextBuffer(ITextSnapshot initialSnapshot) : ITextBuffer
{
	private ITextSnapshot currentSnapshot = initialSnapshot;
	public ITextSnapshot CurrentSnapshot => currentSnapshot;

	public event EventHandler<TextContentChangedEventArgs>? Changed;

	public void UpdateSnapshot(ITextSnapshot newSnapshot, IReadOnlyList<TextChange> changes)
	{
		var oldSnapshot = currentSnapshot;
		currentSnapshot = newSnapshot;
		Changed?.Invoke(this, new TextContentChangedEventArgs(oldSnapshot, newSnapshot, changes));
	}
}

public class SharpIdeTextSnapshot(string text) : ITextSnapshot
{
	public SourceText AsText() => SourceText.From(text);
}
