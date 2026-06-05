using Godot;

namespace SharpIDE.Godot.Features.Tools;

public sealed record IdeToolInstance(IdeToolId Id, Control Control, Texture2D Icon);