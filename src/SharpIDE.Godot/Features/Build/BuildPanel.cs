using System.Threading.Channels;
using GDExtensionBindgen;
using Godot;
using SharpIDE.Application.Features.Build;
using SharpIDE.Godot.Features.TerminalBase;

namespace SharpIDE.Godot.Features.Build;

public partial class BuildPanel : Control
{
    private SharpIdeTerminal _terminal = null!;
    private ChannelReader<string>? _buildOutputChannelReader;
    
	[Inject] private readonly BuildService _buildService = null!;
    public override void _Ready()
    {
        _terminal = GetNode<SharpIdeTerminal>("%SharpIdeTerminal");
        _buildService.BuildStarted.Subscribe(OnBuildStarted);
    }

    public override void _Process(double delta)
    {
        if (_buildOutputChannelReader is null) return;
        // TODO: Buffer and write once per frame? Investigate if godot-xterm already buffers internally
        while (_buildOutputChannelReader.TryRead(out var str))
        {
            _terminal.Write(str);
        }
    }

    private async Task OnBuildStarted(BuildStartedFlags _)
    {
        await this.InvokeAsync(() => _terminal.ClearTerminal());
        _buildOutputChannelReader ??= _buildService.BuildTextWriter.ConsoleChannel.Reader;
    }
}