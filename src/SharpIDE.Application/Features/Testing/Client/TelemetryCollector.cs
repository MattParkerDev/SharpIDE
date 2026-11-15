using System.Collections.Concurrent;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Application.Features.Testing.Client;

public class TelemetryCollector : ConcurrentBag<TelemetryPayload>;
