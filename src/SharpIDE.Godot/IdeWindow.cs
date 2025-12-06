using Godot;
using Microsoft.Extensions.Hosting;
using SharpIDE.Application.Features.Build;
using SharpIDE.Godot.Features.IdeSettings;
using SharpIDE.Godot.Features.SlnPicker;
using Environment = System.Environment;

namespace SharpIDE.Godot;

/// <summary>
/// Used to hold either the main IDE scene or the solution picker scene
/// </summary>
public partial class IdeWindow : Control
{
    private const string SlnPickerScenePath = "res://Features/SlnPicker/SlnPicker.tscn";
    private const string IdeRootScenePath = "res://IdeRoot.tscn";
    private PackedScene? _solutionPickerScene;
    private PackedScene? _ideRootScene;

    private IdeRoot? _ideRoot;
    private SlnPicker? _slnPicker;
    
    public override void _Ready()
    {
        GD.Print("IdeWindow _Ready called");
        ResourceLoader.LoadThreadedRequest(SlnPickerScenePath);
        ResourceLoader.LoadThreadedRequest(IdeRootScenePath);
        SetMaxFpsForMonitor();
        // Godot doesn't have an easy equivalent of launchsettings.json, and we also want this to be set for published builds
        Environment.SetEnvironmentVariable("MSBUILD_PARSE_SLN_WITH_SOLUTIONPERSISTENCE", "1");
        SharpIdeMsbuildLocator.Register();
        GodotOtelExtensions.AddServiceDefaults();
        Singletons.AppState = AppStateLoader.LoadAppStateFromConfigFile();
        GetTree().GetRoot().ContentScaleFactor = Singletons.AppState.IdeSettings.UiScale;
        //GetWindow().SetMinSize(new Vector2I(1152, 648));
        Callable.From(() => PickSolution(true)).CallDeferred();
    }
    
    public override void _ExitTree()
    {
        AppStateLoader.SaveAppStateToConfigFile(Singletons.AppState);
        // GodotGlobalEvents.Instance = null!;
        // GlobalEvents.Instance = null!;
        // GC.Collect();
        // GC.WaitForPendingFinalizers();
        // GC.Collect();
        // PrintOrphanNodes();
    }

    private void SetMaxFpsForMonitor()
    {
        // Keep max fps below display refresh rate for gsync, to avoid input lag. Currently does not handle window moving across monitors
        var refreshRate = (int)DisplayServer.ScreenGetRefreshRate();
        var refreshRateBelowCap = refreshRate - (refreshRate * refreshRate) / 3600.0;
        var refreshRateBelowCapRoundedDownToInt = (int)refreshRateBelowCap;
        GD.Print($"Detected display refresh rate: {refreshRate} Hz, setting max FPS to {refreshRateBelowCapRoundedDownToInt} Hz");
        Engine.MaxFps = refreshRateBelowCapRoundedDownToInt;
    }
    
    public void PickSolution(bool fullscreen = false)
    {
        if (_slnPicker is not null) throw new InvalidOperationException("Solution picker is already active");
        _solutionPickerScene ??= (PackedScene)ResourceLoader.LoadThreadedGet(SlnPickerScenePath);
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
            _ideRootScene ??= (PackedScene)ResourceLoader.LoadThreadedGet(IdeRootScenePath);
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
            var recentSln = Singletons.AppState.RecentSlns.SingleOrDefault(s => s.FilePath == slnPath);
            if (recentSln is not null)
            {
                Singletons.AppState.RecentSlns.Remove(recentSln);
            }
            recentSln ??= new RecentSln { FilePath = slnPath, Name = Path.GetFileName(slnPath)};
            Singletons.AppState.RecentSlns.Add(recentSln);
            
            await this.InvokeAsync(() =>
            {
                if (fullscreen is false) _slnPicker.GetParent<Window>().Hide();
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
                _ideRoot = ideRoot; // This has no DI services, until it is added to the scene tree
                GetNode<DiAutoload>("/root/DiAutoload").ResetScope();
                AddChild(ideRoot);
            });
        });
    }
}