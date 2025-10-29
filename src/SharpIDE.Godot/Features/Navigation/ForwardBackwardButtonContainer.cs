using Godot;
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
    }
}