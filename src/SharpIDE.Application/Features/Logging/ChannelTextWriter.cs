using System.Text;
using System.Threading.Channels;

namespace SharpIDE.Application.Features.Logging;

// I could do this, or provide an XTermWriter. 🤷‍♂️
public class ChannelTextWriter : TextWriter
{
	public override Encoding Encoding { get; } = Encoding.UTF8;
	public Channel<string> ConsoleChannel { get; } = Channel.CreateUnbounded<string>();

	public override void Write(char value)
	{
		ConsoleChannel.Writer.TryWrite(value.ToString());
	}

	public override void Write(string? value)
	{
		ConsoleChannel.Writer.TryWrite(value!);
	}
	public override void WriteLine(string? value)
	{
		ConsoleChannel.Writer.TryWrite($"{value}\r\n");
	}
	public override void WriteLine(char value)
	{
		throw new NotImplementedException();
	}
	public override void WriteLine()
	{
		throw new NotImplementedException();
	}
}
