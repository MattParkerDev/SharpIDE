using System.IO.Pipelines;
using System.Text;

namespace SharpIDE.MsBuildHost;

public class PipeTextWriter : TextWriter
{
	private readonly Pipe _pipe = new();
	private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();

	public override Encoding Encoding => Encoding.UTF8;

	public PipeReader Reader => _pipe.Reader;

	public override void Write(char value)
	{
		Span<char> chars = stackalloc char[1] { value };
		Write(chars);
	}

	public override void Write(string? value)
	{
		if (value is null) return;
		Write(value.AsSpan());
	}

	public override void WriteLine(string? value)
	{
		Write(value);
		Write("\r\n");
	}

	public override void Write(ReadOnlySpan<char> chars)
	{
		var writer = _pipe.Writer;

		var maxBytes = Encoding.GetMaxByteCount(chars.Length);
		var buffer = writer.GetSpan(maxBytes);

		_encoder.Convert(
			chars,
			buffer,
			flush: false,
			out int charsUsed,
			out int bytesUsed,
			out _);

		writer.Advance(bytesUsed);
		_ = writer.FlushAsync();
	}
}
