using Godot;
using System;

public partial class SolutionDialog : AcceptDialog
{
	
	private FileDialog _folderDialog = null!;
	private Tree _projectTypeList = null!;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_folderDialog = GetNode<FileDialog>("%FolderDialog");
	}
}
