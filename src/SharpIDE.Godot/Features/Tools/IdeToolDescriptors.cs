using SharpIDE.Godot.Features.Layout.Resources;

namespace SharpIDE.Godot.Features.Tools;

public static class IdeToolDescriptors
{
    public static readonly IReadOnlyDictionary<IdeToolId, IdeToolDescriptor> Descriptors =
        new Dictionary<IdeToolId, IdeToolDescriptor>
        {
            [IdeToolId.SolutionExplorer] = new(
                IdeToolId.SolutionExplorer,
                Icons.SolutionExplorer,
                Scenes.SolutionExplorer),
            [IdeToolId.Problems] = new(
                IdeToolId.Problems,
                Icons.Problems,
                Scenes.Problems),
            [IdeToolId.Run] = new(
                IdeToolId.Run,
                Icons.Run,
                Scenes.Run),
            [IdeToolId.Debug] = new(
                IdeToolId.Debug,
                Icons.Debug,
                Scenes.Debug),
            [IdeToolId.Build] = new(
                IdeToolId.Build,
                Icons.Build,
                Scenes.Build),
            [IdeToolId.Nuget] = new(
                IdeToolId.Nuget,
                Icons.Nuget,
                Scenes.Nuget),
            [IdeToolId.TestExplorer] = new(
                IdeToolId.TestExplorer,
                Icons.TestExplorer,
                Scenes.TestExplorer),
            [IdeToolId.IdeDiagnostics] = new(
                IdeToolId.IdeDiagnostics,
                Icons.IdeDiagnostics,
                Scenes.IdeDiagnostics)
        };
}