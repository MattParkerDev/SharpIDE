using Godot;
using SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

namespace SharpIDE.Godot.Features.CodeEditor;

file enum TabContextMenuOptions
{
	Close = 0,
	CloseOtherTabs = 1,
	CloseAllTabs = 2,
	CopyFullPath = 3,
	RenameFile = 4,
	RevealInFileExplorer = 5
}

public partial class CodeEditorPanel
{
	private readonly PackedScene _renameFileDialogScene = ResourceLoader.Load<PackedScene>("uid://b775b5j4rkxxw");

	private void OpenContextMenuTab(long tabIndex)
	{
		var menu = new PopupMenu();
		AddChild(menu);
		menu.AddItem("Close", (int)TabContextMenuOptions.Close);
		menu.AddItem("Close Other Tabs", (int)TabContextMenuOptions.CloseOtherTabs);
		menu.AddItem("Close All Tabs", (int)TabContextMenuOptions.CloseAllTabs);
		menu.AddSeparator();
		menu.AddItem("Copy Full Path", (int)TabContextMenuOptions.CopyFullPath);
		menu.AddSeparator();
		menu.AddItem("Rename File", (int)TabContextMenuOptions.RenameFile);
		menu.AddSeparator();
		menu.AddItem("Reveal in File Explorer", (int)TabContextMenuOptions.RevealInFileExplorer);
		menu.PopupHide += () => menu.QueueFree();
		menu.IdPressed += id =>
		{
			var sharpIdeCodeEditContainer = (SharpIdeCodeEditContainer)_tabContainer.GetTabControl((int)tabIndex);
			var file = sharpIdeCodeEditContainer.CodeEdit.SharpIdeFile;

			var actionId = (TabContextMenuOptions)id;
			if (actionId is TabContextMenuOptions.Close)
			{
				CloseTabs([sharpIdeCodeEditContainer]);
			}
			else if (actionId is TabContextMenuOptions.CloseOtherTabs)
			{
				var otherTabs = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().Except([sharpIdeCodeEditContainer]).ToList();
				CloseTabs(otherTabs);
			}
			else if (actionId is TabContextMenuOptions.CloseAllTabs)
			{
				var allTabs = _tabContainer.GetChildren().OfType<SharpIdeCodeEditContainer>().ToList();
				CloseTabs(allTabs);
			}
			else if (actionId is TabContextMenuOptions.CopyFullPath)
			{
				DisplayServer.ClipboardSet(file.Path);
			}
			else if (actionId is TabContextMenuOptions.RenameFile)
			{
				var renameFileDialog = _renameFileDialogScene.Instantiate<RenameFileDialog>();
				renameFileDialog.File = file;
				AddChild(renameFileDialog);
				renameFileDialog.PopupCentered();
			}
			else if (actionId is TabContextMenuOptions.RevealInFileExplorer)
			{
				OS.ShellShowInFileManager(file.Path);
			}
		};

		var globalMousePosition = GetGlobalMousePosition();
		menu.Position = new Vector2I((int)globalMousePosition.X, (int)globalMousePosition.Y);
		menu.Popup();
	}
}