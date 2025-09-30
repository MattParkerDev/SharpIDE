using Ardalis.GuardClauses;
using Godot;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Hosting;
using SharpIDE.Godot.Features.SlnPicker;

namespace SharpIDE.Godot;

/// <summary>
/// Used to hold either the main IDE scene or the solution picker scene
/// </summary>
public partial class IdeWindow : Control
{
    private readonly PackedScene _solutionPickerScene = ResourceLoader.Load<PackedScene>("res://Features/SlnPicker/SlnPicker.tscn");
    private readonly PackedScene _ideRootScene = ResourceLoader.Load<PackedScene>("res://IdeRoot.tscn");

    private IdeRoot? _ideRoot;
    private SlnPicker? _slnPicker;

    public override void _Ready()
    {
        MSBuildLocator.RegisterDefaults();
        GodotServiceDefaults.AddServiceDefaults();
        //GetWindow().SetMinSize(new Vector2I(1152, 648));
        
        PickSolution(true);
    }
    
    public void PickSolution(bool fullscreen = false)
    {
        if (_slnPicker is not null) throw new InvalidOperationException("Solution picker is already active");
        _slnPicker = _solutionPickerScene.Instantiate<SlnPicker>();
        if (fullscreen)
        {
            AddChild(_slnPicker);
        }
        else
        {
            var popupWindow = GetNode<Window>("Window");
            var windowSize = GetWindow().GetSize();
            popupWindow.Size = windowSize with { X = windowSize.X / 2, Y = windowSize.Y / 2 };
            popupWindow.Title = "Open Solution";
            popupWindow.AddChild(_slnPicker);
            popupWindow.Popup();
            popupWindow.CloseRequested += () =>
            {
                popupWindow.Hide();
            };
        }
        _ = Task.GodotRun(async () =>
        {
            var slnPathTask = _slnPicker.GetSelectedSolutionPath();
            var ideRoot = _ideRootScene.Instantiate<IdeRoot>();
            ideRoot.IdeWindow = this;
            var slnPath = await slnPathTask;
            if (slnPath is null)
            {
                ideRoot.QueueFree();
                _slnPicker.QueueFree();
                _slnPicker = null;
                return;
            }
            ideRoot.SetSlnFilePath(slnPath);
            
            await this.InvokeAsync(() =>
            {
                _slnPicker.GetParent().RemoveChild(_slnPicker);
                _slnPicker.QueueFree();
                _slnPicker = null;
                if (_ideRoot is not null)
                {
                    RemoveChild(_ideRoot);
                    _ideRoot.QueueFree();
                }
                else
                { 
                    GetWindow().Mode = Window.ModeEnum.Maximized;
                }
                _ideRoot = ideRoot;
                AddChild(ideRoot);
            });
        });
    }
}