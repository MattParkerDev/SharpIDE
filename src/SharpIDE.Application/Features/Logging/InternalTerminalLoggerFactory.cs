using Ardalis.GuardClauses;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace SharpIDE.Application.Features.Logging;

public class InternalTerminalLoggerFactory
{
	public static ILogger CreateLogger(TextWriter output)
	{
		var logger = CreateLogger("FORCECONSOLECOLOR", LoggerVerbosity.Minimal, output);
		return logger;
	}

	private static ILogger CreateLogger(string parameters, LoggerVerbosity loggerVerbosity, TextWriter output)
	{
		string[]? args = [];
		bool supportsAnsi = true;
		bool outputIsScreen = true;
		uint? originalConsoleMode = 0x0007;

		//var logger = new TerminalLogger(loggerVerbosity, originalConsoleMode);
		var terminal = new Terminal(output);
		var logger = new TerminalLogger(terminal);
		var field = typeof(TerminalLogger).GetField("_manualRefresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		Guard.Against.Null(field, nameof(field));
		field.SetValue(logger, false);

		//var logger = TerminalLogger.CreateTerminalOrConsoleLogger(args, supportsAnsi, outputIsScreen, originalConsoleMode);

		logger.Parameters = parameters;
		logger.Verbosity = loggerVerbosity;
		return logger;
	}
}
