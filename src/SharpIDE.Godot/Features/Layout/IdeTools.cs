using Godot;

namespace SharpIDE.Godot.Features.Layout;

public enum IdeTool
{
    SolutionExplorer,
    Problems,
    Run,
    Debug,
    Build,
    Nuget,
    TestExplorer,
}

public sealed record IdeToolData(Texture2D Icon, PackedScene Scene);

public static class IdeTools
{
    public static readonly IReadOnlyDictionary<IdeTool, IdeToolData> ToolDataMap = new Dictionary<IdeTool, IdeToolData>
    {
        [IdeTool.SolutionExplorer] = new(Load<Texture2D>("uid://ccj0dw81x3bkc"), Load<PackedScene>("uid://cy1bb32g7j7dr")),
        [IdeTool.Problems] = new(Load<Texture2D>("uid://uukf1nwjhthv"), Load<PackedScene>("uid://tqpmww430cor")),
        [IdeTool.Run] = new(Load<Texture2D>("uid://cre7q0efp4vrq"), Load<PackedScene>("uid://bcoytt3bw0gpe")),
        [IdeTool.Debug] = new(Load<Texture2D>("uid://butisxqww0boc"), Load<PackedScene>("uid://dkjips8oudqou")),
        [IdeTool.Build] = new(Load<Texture2D>("uid://b0170ypw8uf3a"), Load<PackedScene>("uid://co6dkhdolriej")),
        [IdeTool.Nuget] = new(Load<Texture2D>("uid://b5ih61vdjv5e6"), Load<PackedScene>("uid://duyxg107nfh2f")),
        [IdeTool.TestExplorer] = new(Load<Texture2D>("uid://dged1mm438qli"), Load<PackedScene>("uid://hwdok1kch3b3")),
    };

    private static TResource Load<TResource>(string resourceUid) where TResource : Resource
    {
        return ResourceLoader.Load<TResource>(resourceUid);
    }
}