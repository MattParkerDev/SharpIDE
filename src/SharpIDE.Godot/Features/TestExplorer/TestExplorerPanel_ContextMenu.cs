using Ardalis.GuardClauses;
using Godot;
using SharpIDE.Application.Features.Testing.Client.Dtos;

namespace SharpIDE.Godot.Features.TestExplorer;

file enum TestNodeContextMenuOptions
{
	Run = 0,
    Debug = 1
}

public partial class TestExplorerPanel
{
    private Texture2D _runIcon = ResourceLoader.Load<Texture2D>("uid://bkty6563cthj8");
    private Texture2D _debugIcon = ResourceLoader.Load<Texture2D>("uid://c7cmou8hipsvc");

    private void OpenContextMenuTestNode(TestNode testNode, Vector2 globalMousePosition)
    {
        Guard.Against.Null(testNode);
        var menu = new PopupMenu();
        AddChild(menu);

        menu.AddIconItem(_runIcon, "Run Unit Tests", (int)TestNodeContextMenuOptions.Run);
        menu.SetItemIconMaxWidth(menu.GetItemIndex((int)TestNodeContextMenuOptions.Run), 20);
        menu.SetItemDisabled(menu.GetItemIndex((int)TestNodeContextMenuOptions.Run), true);

        menu.AddIconItem(_debugIcon, "Debug Unit Tests", (int)TestNodeContextMenuOptions.Debug);
		menu.SetItemIconMaxWidth(menu.GetItemIndex((int)TestNodeContextMenuOptions.Debug), 20);
        menu.SetItemDisabled(menu.GetItemIndex((int)TestNodeContextMenuOptions.Debug), true);

        menu.PopupHide += menu.QueueFree;
        menu.IdPressed += id =>
        {
            var actionId = (TestNodeContextMenuOptions)id;
            if (actionId is TestNodeContextMenuOptions.Run)
            {
                _ = Task.GodotRun(async () =>
                {

                });
            }
            else if (actionId is TestNodeContextMenuOptions.Debug)
            {
	            _ = Task.GodotRun(async () =>
				{

				});
            }
        };

        menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
        menu.Popup();
    }
}
