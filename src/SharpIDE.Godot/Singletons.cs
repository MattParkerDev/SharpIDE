using NuGet.Versioning;
using SharpIDE.Godot.Features.IdeSettings;

namespace SharpIDE.Godot;

public static class Singletons
{
    public static AppState AppState { get; set; } = null!;
    public static NuGetVersion SharpIdeVersion { get; set; } = null!;
}