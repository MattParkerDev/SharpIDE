// using System.Collections.Immutable;
// using Microsoft.CodeAnalysis.Text;
//
// namespace SharpIDE.Application.Features.Analysis;
//
// public class MyTextContainer : SourceTextContainer
// {
// 	private SourceText _currentText;
//
// 	public MyTextContainer(IMyTextBuffer buffer)
// 	{
// 		_currentText = SourceText.From(buffer.GetText());
// 	}
//
// 	public override SourceText CurrentText => _currentText;
//
// 	public override event EventHandler<TextChangeEventArgs>? TextChanged;
//
// 	// Call this when your buffer changes
// 	public void RaiseTextChanged(MyTextChangedEventArgs bufferEvent)
// 	{
// 		var oldText = _currentText;
// 		_currentText = SourceText.From(bufferEvent.NewText);
//
// 		// Convert your buffer's change event to Roslyn's format
// 		var textChangeRange = new TextChangeRange(
// 			new TextSpan(bufferEvent.Start, bufferEvent.OldLength),
// 			bufferEvent.NewLength);
//
// 		TextChanged?.Invoke(this,
// 			new TextChangeEventArgs(oldText, _currentText,
// 				ImmutableArray.Create(textChangeRange)));
// 	}
// }
