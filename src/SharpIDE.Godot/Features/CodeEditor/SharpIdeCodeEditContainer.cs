using Godot;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEditContainer : VBoxContainer
{
    [Export] 
    public SharpIdeCodeEdit CodeEdit { get; set; } = null!;

    public override void _Ready()
    {
        
    }
}