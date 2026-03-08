using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.TerminalBase;

namespace SharpIDE.Godot.Features.Run;

public partial class RunPanelTab : Control
{
	private SharpIdeTerminal _terminal = null!;
	private Task _writeTask = Task.CompletedTask;
    
    public SharpIdeProjectModel Project { get; set; } = null!;
    public int TabBarTab { get; set; }

    public override void _Ready()
    {
	    _terminal = GetNode<SharpIdeTerminal>("SharpIdeTerminal");
    }
    
    public void StartWritingFromProjectOutput()
	{
		if (_writeTask.IsCompleted is not true)
		{
			GD.PrintErr("Attempted to start writing from project output, but a write task is already running.");
			return;
		}
		_writeTask = Task.GodotRun(async () =>
		{
			await foreach (var array in Project.RunningOutputChannel!.Reader.ReadAllAsync().ConfigureAwait(false))
			{
				_terminal.Write(array);
			}
		});
	}
    
	public void ClearTerminal()
	{
		_terminal.ClearTerminal();
	}
}