using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;
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

    public void StartProjectProcessIo()
    {
        if (_writeTask.IsCompleted is not true)
        {
            GD.PrintErr("Attempted to start writing from project output, but a write task is already running.");
            return;
        }
        Guard.Against.Null(Project.ProcessStandardIo);
        Guard.Against.Null(Project.ProcessStandardIo.OutputReader);
        Guard.Against.Null(Project.ProcessStandardIo.StdinWriter);
        _terminal.InputWriter = Project.ProcessStandardIo.StdinWriter;
        _writeTask = Task.GodotRun(async () =>
        {
            var reader = Project.ProcessStandardIo.OutputReader;
            Guard.Against.Null(reader);
            while (true)
            {
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;
                foreach (var segment in buffer)
                {
                    _terminal.Write(segment.Span);
                }
                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted) break;
            }
            await reader.CompleteAsync();
            Project.ProcessStandardIo.OutputReadComplete.SetResult();
            _terminal.InputWriter = null;
            await Project.ProcessStandardIo.StdinWriter.CompleteAsync();
            Project.ProcessStandardIo.StdinWriteComplete.SetResult();
        });
    }

    public void ClearTerminal()
    {
        _terminal.ClearTerminal();
    }
}
