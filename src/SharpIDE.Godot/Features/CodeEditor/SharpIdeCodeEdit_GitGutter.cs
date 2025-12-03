using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.Git;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
	private int _gitGutterId;
    
    private void AddGitGutter()
    {
        AddGutter(-1);
        var gutterId = GetGutterCount() - 1;
        SetGutterType(gutterId, GutterType.Custom);
        SetGutterCustomDraw(gutterId, new Callable(this, MethodName.GitGutterCustomDraw));
        SetGutterDraw(gutterId, true);
        SetGutterWidth(gutterId, 9);
        SetGutterClickable(gutterId, true);
        GutterClicked += OnGutterClicked;
        _gitGutterId = gutterId;
    }
    
    private void OnGutterClicked(long line, long gutterId)
    {
        if (gutterId != _gitGutterId) return;
        GD.Print($"Git gutter clicked at line {line}");
    }

    private void GitGutterCustomDraw(int lineIndex, int gutterIndex, Rect2 rect)
    {
        var gitChangeType = GitLineStatus.Added;
        var color = GitColours.GetColorForGitLineStatus(gitChangeType);
		
        var mousePos = GetLocalMousePosition();
        var isHovered = rect.HasPoint(mousePos);
        var width = isHovered ? 9 : 6;
        var drawRect = new Rect2(new Vector2(rect.End.X - width, rect.Position.Y), new Vector2(width, rect.Size.Y)
        );
        DrawRect(drawRect, color);
    }
}