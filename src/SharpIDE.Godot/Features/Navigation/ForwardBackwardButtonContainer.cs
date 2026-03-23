using Godot;
using R3;
using SharpIDE.Application.Features.NavigationHistory;

namespace SharpIDE.Godot.Features.Navigation;

public partial class ForwardBackwardButtonContainer : HBoxContainer
{
    private Button _backwardButton = null!;
    private Button _forwardButton = null!;
    
    [Inject] private readonly IdeNavigationHistoryService _navigationHistoryService = null!;

    public override void _Ready()
    {
        _backwardButton = GetNode<Button>("BackwardButton");
        _forwardButton = GetNode<Button>("ForwardButton");
        _backwardButton.Pressed += OnBackwardButtonPressed;
        _forwardButton.Pressed += OnForwardButtonPressed;
        _navigationHistoryService.Current.SubscribeOnThreadPool().ObserveOnThreadPool().SubscribeAwait(async (s, ct) =>
        {
            await this.InvokeAsync(() =>
            {
                _backwardButton.Disabled = !_navigationHistoryService.CanGoBack;
                _forwardButton.Disabled = !_navigationHistoryService.CanGoForward;
            });
        }, configureAwait: false).AddTo(this);
    }
    
    private void OnBackwardButtonPressed()
    {
        _navigationHistoryService.GoBack();
        var current = _navigationHistoryService.Current.Value;
        GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(current!.File, current.LinePosition);
    }

    private void OnForwardButtonPressed()
    {
        _navigationHistoryService.GoForward();
        var current = _navigationHistoryService.Current.Value;
        GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(current!.File, current.LinePosition);
    }
}