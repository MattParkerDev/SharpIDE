using Godot;

namespace SharpIDE.Godot.Features.Tools;

public sealed record IdeToolDescriptor(IdeToolId Id, Texture2D Texture, PackedScene Scene)
{
    public IdeToolId Id { get; } = Id;

    public Texture2D Icon { get; } = Texture;

    public PackedScene Scene { get; } = Scene;
}