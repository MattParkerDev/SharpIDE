namespace SharpIDE.Godot.Features.About;

public enum UpdateStage
{
    Idle,
    Checking,
    UpdateAvailable,
    NoUpdateFound,
    Downloading,
    ReadyToInstall,
    Installing,
    Failed
}
