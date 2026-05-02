using Godot;
using System;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot;
using SharpIDE.Godot.Features.Run;

public partial class CustomRunButton : Button
{
	[Signal]
	public delegate void ProjectChangedEventHandler();

	[Signal]
	public delegate void ItemAddedEventHandler(int index);

	[Signal]
	public delegate void RunRequestedEventHandler(RunMenuItem item);
	
	private readonly PackedScene _runMenuItemScene = ResourceLoader.Load<PackedScene>("res://Features/Run/RunMenuItem.tscn");
	private Popup _runMenuPopup = null!;
	private VBoxContainer _runOptions = null!;
	
	private RunOption _currentRunOption = null!;

	public RunOption CurrentRunOption
	{
		get => _currentRunOption;
		set
		{
			_currentRunOption = value;
			Callable.From<string?>((name) => Text = name).CallDeferred(_currentRunOption?.Name ?? "");
		}
	}

	public List<RunOption> Options = [];
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_runMenuPopup = GetNode<Popup>("%RunMenuPopup");
		_runOptions = GetNode<VBoxContainer>("%RunOptions");
		UpdateMinimumSize();
		if (CurrentRunOption != null)
			Text = CurrentRunOption.Name;

		foreach (var option in Options)
		{
			AddOption(option.Model);
		}

		Pressed += HandlePopupMenu;
		
		if (GetParent() is not BoxContainer parent) return;
		parent.Resized += HandleSizeChanged;
	}

	public void AddOption(SharpIdeProjectModel model) => AddOption(model.Name.Value, model);

	public void AddOption(string name, SharpIdeProjectModel model)
	{
		var indx = Options.Count;
		var ro = new RunOption(name, model);
		Options.Add(ro);
		var item = _runMenuItemScene.Instantiate<RunMenuItem>();
		item.Project = model;
		item.Pressed += () => HandleOptionPressed(ro);
		item.RunRequested += EmitSignalRunRequested;
		_runOptions.AddChild(item);
		UpdateMinimumSize();
		EmitSignalItemAdded(indx);
	}

	public void RemoveOption(SharpIdeProjectModel model)
	{
		RunOption foundOption = null!;
		foreach (var option in Options.Where(option => option.Model == model))
		{
			foreach (var menuItem in _runOptions.GetChildren())
			{
				if (menuItem is not RunMenuItem rmi) continue;
				if (rmi.Project != model) continue;
				rmi.QueueFree();
				break;
			}
			foundOption = option;
		}

		Options.Remove(foundOption);
		UpdateMinimumSize();
	}

	public void RemoveOption(RunOption option)
	{
		foreach (var menuItem in _runOptions.GetChildren())
		{
			if (menuItem is not RunMenuItem rmi) continue;
			if (rmi.Project != option.Model) continue;
			rmi.QueueFree();
		}

		Options.Remove(option);
		UpdateMinimumSize();
	}

	public void RemoveOption(string name)
	{
		RunOption foundOption = Options.Where(option => option.Name == name).FirstOrDefault();
		if (foundOption != null)
			RemoveOption(foundOption);
	}

	public void SelectOption(SharpIdeProjectModel model)
	{
		CurrentRunOption = Options.FirstOrDefault(option => option.Model == model)!;
	}

	public void SelectOption(string name = "", string filePath = "")
	{
		if (name == "" && filePath == "") return;
		if (name != "")
			CurrentRunOption = Options.FirstOrDefault(option => option.Name == name)!;
		else
			CurrentRunOption = Options.FirstOrDefault(option => option.Model.FilePath == filePath)!;
	}

	public void SelectOption(int index)
	{
		if (index >= 0 && index < Options.Count)
			CurrentRunOption = Options[index];
	}
	
	public void SelectOption(RunOption option) => CurrentRunOption = option;

	private void UpdateMinimumSize()
	{
		var font = GetThemeDefaultFont();
		var size = GetThemeDefaultFontSize();
		var maxWidth = 0f;
		foreach (var option in Options)
		{
			var measure = font.GetStringSize(option.Name, fontSize: size);
			if (measure.X + 42 > maxWidth)
				maxWidth = measure.X + 42;
		}

		CustomMinimumSize = new Vector2(maxWidth, 0);
	}

	private void HandleSizeChanged()
	{
		if (GetParent() is not Control parent) return;
		var space = parent.Size;
		var popupSize = new Vector2I((int)space.X+3, _runMenuPopup.Size.Y);
		_runMenuPopup.MinSize = popupSize;
		_runMenuPopup.Size = popupSize;
	}

	private void HandlePopupMenu()
	{
		var popupMenuPosition = GlobalPosition;
		const int buttonHeight = 39;
		_runMenuPopup.Position = new Vector2I((int)popupMenuPosition.X, (int)popupMenuPosition.Y + buttonHeight);
		_runMenuPopup.Popup();
	}

	private void HandleOptionPressed(RunOption option)
	{
		CurrentRunOption = option;
		Text = option.Name;
		_runMenuPopup.Hide();
		EmitSignalProjectChanged();
	}
}


public class RunOption
{
	public string Name = null!;
	public SharpIdeProjectModel Model = null!;

	public RunOption(SharpIdeProjectModel model)
	{
		Model = model;
		Name = model.Name.Value;
	}

	public RunOption(string name, SharpIdeProjectModel model)
	{
		Model = model;
		Name = name;
	}

	public RunOption()
	{
		
	}
}