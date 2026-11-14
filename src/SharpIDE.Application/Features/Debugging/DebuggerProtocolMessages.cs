using System.Text.Json.Serialization;
using SharpDbg.DapProtocol;
using SharpIDE.Application.Features.Debugging.Signing;

namespace SharpIDE.Application.Features.Debugging;

public sealed record HandshakeArguments : IDapRequest<HandshakeResponse>, IDapNamedMessage
{
	public const string CommandName = "handshake";
	static string IDapNamedMessage.MessageName => CommandName;
	string IDapRequest.Command => CommandName;

	[JsonPropertyName("value")]
	public required string Value { get; init; }
}

public sealed record HandshakeResponse
{
	[JsonPropertyName("signature")]
	public required string Signature { get; init; }
}

/// <summary>
/// Handles requests sent from the debug adapter to SharpIDE.
/// </summary>
internal sealed class DebuggerClientRequestHandler : IDapRequestHandler
{
	public ValueTask<DapResponse> HandleRequestAsync(IDapRequest request, DapRequestContext context) => request switch
	{
		HandshakeArguments args => DapResponse.Respond(args, HandleHandshakeAsync),
		_ => DapResponse.NotSupported(request),
	};

	private static async ValueTask<DapResult<HandshakeResponse>> HandleHandshakeAsync(HandshakeArguments arguments)
	{
		var signature = await DebuggerHandshakeSigner.Sign(arguments.Value);
		return new HandshakeResponse { Signature = signature };
	}
}

/// <summary>
/// Thrown when the debug adapter responds to a request with an error.
/// </summary>
public sealed class DebuggerRequestFailedException(string command, DapError error) : Exception(error.Message)
{
	public string Command { get; } = command;
	public DapError Error { get; } = error;
}

/// <summary>
/// Presentation hint values sent by sharpdbg that are not part of the DAP specification.
/// </summary>
public static class DebuggerPresentationHints
{
	public static VariablePresentationHint.AttributesValue FailedEvaluation { get; } = new("failedEvaluation");
}
