namespace SharpIDE.Application.Features.Build;

public sealed class MsBuildHostStartException(string message, Exception? innerException = null) : Exception(message, innerException);
