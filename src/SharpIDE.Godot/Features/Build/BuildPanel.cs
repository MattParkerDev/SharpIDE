using System.IO.Pipelines;
using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Godot.Features.TerminalBase;

namespace SharpIDE.Godot.Features.Build;

public partial class BuildPanel : Control
{
    private SharpIdeTerminal _terminal = null!;
    private PipeReader? _buildOutputPipeReader;
    
	[Inject] private readonly BuildService _buildService = null!;
    public override void _Ready()
    {
        _terminal = GetNode<SharpIdeTerminal>("%SharpIdeTerminal");
        _buildService.BuildStarted.Subscribe(OnBuildStarted);
    }

    public override void _Process(double delta)
    {
        if (_buildOutputPipeReader is null) return;
        if (_buildOutputPipeReader.TryRead(out var readResult) is not true) return;
        
        var byteSequence = readResult.Buffer;
        if (byteSequence.IsEmpty)
        {
            _buildOutputPipeReader.AdvanceTo(byteSequence.End);
            return;
        }
        foreach (var segment in byteSequence)
        { 
            // TODO: Buffer and write once per frame? Investigate if godot-xterm already buffers internally
            _terminal.Write(segment.Span);
        }
        _buildOutputPipeReader.AdvanceTo(byteSequence.End);
    }

    private async Task OnBuildStarted(BuildStartedFlags _)
    {
        await this.InvokeAsync(() => _terminal.ClearTerminal());
        _buildOutputPipeReader = _buildService.BuildLogPipeReader; // A new reader if the host was restarted
    }
}