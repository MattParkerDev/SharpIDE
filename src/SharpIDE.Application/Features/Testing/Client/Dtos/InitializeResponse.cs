namespace SharpIDE.Application.Features.Testing.Client.Dtos;

public sealed record InitializeResponse(
    ServerInfo ServerInfo,
    ServerCapabilities Capabilities);
