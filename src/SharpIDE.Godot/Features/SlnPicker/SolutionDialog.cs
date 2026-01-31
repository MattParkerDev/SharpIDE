using Godot;
using System;
using SharpIDE.Godot.Features.SlnPicker;

public partial class SolutionDialog : AcceptDialog
{
	
	private FileDialog _folderDialog = null!;
	private TabContainer _contentSwitcher = null!;
	private Button _slnDirOpenButton = null!;
	private LineEdit _slnDirLineEdit = null!;
	private Label _prjTypeLabel = null!;
	private MarginContainer _panel2MarginContainer = null!;
	private OptionButton _sdkVersionOptions = null!;

	public override void _Ready()
	{
		var creatorNode = new Node();
		var projectCreator = new ProjectCreator();
		creatorNode.AddChild(projectCreator);
		
		_folderDialog = GetNode<FileDialog>("%FolderDialog");
		_panel2MarginContainer = GetNode<MarginContainer>("%Panel2MarginContainer");
		_contentSwitcher = GetNode<TabContainer>("%PrjSettingsTabContainer");
		_slnDirOpenButton = GetNode<Button>("%SlnDirOpenButton");
		_slnDirLineEdit = GetNode<LineEdit>("%SlnDirLineEdit");
		_prjTypeLabel = GetNode<Label>("%PrjTypeLabel");
		_sdkVersionOptions = GetNode<OptionButton>("%SdkVersionOptions");
		_panel2MarginContainer.Visible = false;
		
		_slnDirOpenButton.Pressed += () => _folderDialog.PopupCentered();
		_folderDialog.DirSelected += (dir) => _slnDirLineEdit.Text = dir;
		GetNode<Button>("%BlazorButton").Pressed += () => OnTypeSelected("BlazorSettingsPanel", "blazor");
		GetNode<Button>("%ConsoleButton").Pressed += () => OnTypeSelected("ConsoleSettingsPanel", "console");

		var dotnetPath = projectCreator.GetDotnetPath();
		var installedSdks = projectCreator.GetInstalledSdks(dotnetPath);
		
		_sdkVersionOptions.Clear();
		var sdkVersions = installedSdks.Split(',');
		for (int i = sdkVersions.Length - 1; i >= 0; i--)
		{
			_sdkVersionOptions.AddItem(sdkVersions[i].Trim());
		}

	}

	private void OnTypeSelected(string panelName, string projectType)
	{
		_panel2MarginContainer.Visible = true;
		for (int i = 0; i < _contentSwitcher.GetTabCount(); i++)
		{
			if (_contentSwitcher.GetTabTitle(i) == panelName)
			{
				_contentSwitcher.CurrentTab = i;
				_prjTypeLabel.Text = projectType;
				return;
			}
		}
		
		
	}
}
