using System.Buffers;

namespace SharpIDE.Godot.Features.TerminalBase;

public partial class SharpIdeTerminal
{
    private bool _previousArrayEndedInCr = false;

    // Unfortunately, although the terminal emulator handles escape sequences etc, it does not handle interpreting \n as \r\n - that is handled by the PTY, which we currently don't use
    // So we need to replace lone \n with \r\n ourselves
    // TODO: Probably run processes with PTY instead, so that this is not needed, and so we can capture user input and Ctrl+C etc
    // 🤖
    private (byte[] array, int length, bool wasRented) ProcessLineEndings(byte[] input)
    {
        if (input.Length == 0) return (input, 0, false);

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
        if (replacementCount == 0) return (input, input.Length, false);

        // Rent array from pool with space for additional \r characters
        var requiredSize = input.Length + replacementCount;
        var result = ArrayPool<byte>.Shared.Rent(requiredSize);
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
                    result[writeIndex++] = (byte)'\r';
                }
            }

            result[writeIndex++] = input[i];
            previousWasCr = input[i] == (byte)'\r';
        }

        return (result, writeIndex, true);
    }
}