using GDExtensionBindgen;
using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Debug_.Tab.SubTabs;
using SharpIDE.Godot.Features.TerminalBase;

namespace SharpIDE.Godot.Features.Debug_.Tab;

public partial class DebugPanelTab : Control
{
    private SharpIdeTerminal _terminal = null!;
    private ThreadsVariablesSubTab _threadsVariablesSubTab = null!;
    private Task _writeTask = Task.CompletedTask;
    
    public SharpIdeProjectModel Project { get; set; } = null!;
    public int TabBarTab { get; set; }

    public override void _EnterTree()
    {
        _threadsVariablesSubTab = GetNode<ThreadsVariablesSubTab>("%ThreadsVariablesSubTab");
        _threadsVariablesSubTab.Project = Project;
    }

    public override void _Ready()
    {
        _terminal = GetNode<SharpIdeTerminal>("%SharpIdeTerminal");
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