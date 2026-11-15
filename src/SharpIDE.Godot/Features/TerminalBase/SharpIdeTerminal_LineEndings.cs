namespace SharpIDE.Godot.Features.TerminalBase;

public partial class SharpIdeTerminal
{
    private bool _previousArrayEndedInCr = false;

    // Although the terminal emulator handles escape sequences etc, it does not handle interpreting \n as \r\n - that is normally handled by a PTY
    // Run/debug processes are attached to a PTY, so this is skipped for them, but output that doesn't come through one (e.g. build output) still needs lone \n replaced with \r\n
    // 🤖
    // processed should be a ReadOnlySpan, but Godot currently can't cast a ReadOnlySpan<T> to Variant, only Span<T> 
    private void ProcessLineEndings(ReadOnlySpan<byte> input, Span<byte> workingBuffer, out Span<byte> processed)
    {
        if (input.Length == 0)
        {
            processed = Span<byte>.Empty;
            return;
        }
        // Count how many \n need to be replaced (those not preceded by \r)
        var replacementCount = 0;
        var previousWasCr = _previousArrayEndedInCr;

        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'\n')
            {
                // Check if it's preceded by \r
                var precededByCr = (i > 0 && input[i - 1] == (byte)'\r') || (i == 0 && previousWasCr);
                if (!precededByCr)
                {
                    replacementCount++;
                }
            }

            previousWasCr = input[i] == (byte)'\r';
        }

        // If no replacements needed, return original array
        if (replacementCount == 0)
        {
            input.CopyTo(workingBuffer);
            processed = workingBuffer[..input.Length];
            return;
        }

        var writeIndex = 0;
        previousWasCr = _previousArrayEndedInCr;

        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == (byte)'\n')
            {
                // Check if it's preceded by \r
                var precededByCr = (i > 0 && input[i - 1] == (byte)'\r') || (i == 0 && previousWasCr);
                if (!precededByCr)
                {
                    workingBuffer[writeIndex++] = (byte)'\r';
                }
            }

            workingBuffer[writeIndex++] = input[i];
            previousWasCr = input[i] == (byte)'\r';
        }

        processed = workingBuffer[..writeIndex];
    }
}