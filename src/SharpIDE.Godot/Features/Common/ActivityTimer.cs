using System.Diagnostics;
using System.Runtime.CompilerServices;

using Godot;

namespace SharpIDE.Godot.Features.Common;

public sealed class ActivityTimer : IDisposable
{
    private readonly string _activity;
    private readonly Stopwatch _sw;
    
    private ActivityTimer(string activity)
    {
        _activity = activity;
        
        GD.Print($"{_activity} starting");
        _sw = Stopwatch.StartNew();
    }
    
    public static ActivityTimer Start([CallerMemberName] string activity = "")
    {
        return new ActivityTimer(activity);
    }
    
    /// <inheritdoc />
    public void Dispose()
    {
        _sw.Stop();
        GD.Print($"{_activity} completed in {_sw.Elapsed.Milliseconds}.{_sw.Elapsed.Microseconds}ms");
    }
}