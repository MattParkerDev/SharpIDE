using Godot;

namespace SharpIDE.Godot.Features.Layout.Resources;

public static class Scenes
{
    public static readonly PackedScene SolutionExplorer = ResourceLoader.Load<PackedScene>("uid://cy1bb32g7j7dr");
    public static readonly PackedScene Problems = ResourceLoader.Load<PackedScene>("uid://tqpmww430cor");
    public static readonly PackedScene Run = ResourceLoader.Load<PackedScene>("uid://bcoytt3bw0gpe");
    public static readonly PackedScene Debug = ResourceLoader.Load<PackedScene>("uid://dkjips8oudqou");
    public static readonly PackedScene Build = ResourceLoader.Load<PackedScene>("uid://co6dkhdolriej");
    public static readonly PackedScene Nuget = ResourceLoader.Load<PackedScene>("uid://duyxg107nfh2f");
    public static readonly PackedScene TestExplorer = ResourceLoader.Load<PackedScene>("uid://hwdok1kch3b3");
    public static readonly PackedScene IdeDiagnostics = ResourceLoader.Load<PackedScene>("uid://b0tjuqq3bca5e");
    public static readonly PackedScene ToolButton = ResourceLoader.Load<PackedScene>("uid://gcpcsulb43in");
}