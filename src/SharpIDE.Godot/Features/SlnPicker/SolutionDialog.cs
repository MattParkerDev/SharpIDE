using Godot;
using System;

public partial class SolutionDialog : AcceptDialog
{
	
	private FileDialog _folderDialog = null!;
	private TabContainer _contentSwitcher = null!;

	public override void _Ready()
	{
		_folderDialog = GetNode<FileDialog>("%FolderDialog");
		_contentSwitcher = GetNode<TabContainer>("%TabContainer");
		
		GetNode<Button>("%BlazorButton").Pressed += () => OnTypeSelected("BlazorSettingsPanel");
		GetNode<Button>("%ConsoleButton").Pressed += () => OnTypeSelected("ConsoleSettingsPanel");
	}

	private void OnTypeSelected(string typeName)
	{
		_contentSwitcher.Visible = true;
		for (int i = 0; i < _contentSwitcher.GetTabCount(); i++)
		{
			if (_contentSwitcher.GetTabTitle(i) == typeName)
			{
				_contentSwitcher.CurrentTab = i;
				return;
			}
		}
		
		GD.Print($"Selected: {typeName}");
	}
}
