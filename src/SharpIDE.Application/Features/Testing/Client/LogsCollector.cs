using System.Collections.Concurrent;

namespace SharpIDE.Application.Features.Testing.Client;

public class LogsCollector : ConcurrentBag<TestingPlatformClient.Log>;
