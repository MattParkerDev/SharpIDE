using System.Diagnostics;
using Godot;
using Octokit;
using SharpIDE.Godot.Features.IdeAutoUpdate;
using Label = Godot.Label;

namespace SharpIDE.Godot.Features.About;

public partial class AutoUpdateComponent : VBoxContainer
{
	private HBoxContainer _checkForUpdatesHBoxContainer = null!;
	private Button _checkForUpdatesButton = null!;
	private Label _lastCheckedAtLabel = null!;
	
	private HBoxContainer _downloadUpdateHBoxContainer = null!;
	private Button _downloadUpdateButton = null!;
	private Label _newerVersionLabel = null!;
	
	private HBoxContainer _noUpdatesFoundHBoxContainer = null!;
	private HBoxContainer _checkingForUpdatesHBoxContainer = null!;
	private HBoxContainer _downloadingUpdateHBoxContainer = null!;
	
	private HBoxContainer _finishUpdateAndRestartHBoxContainer = null!;
	private Button _finishUpdateAndRestartButton = null!;
	
	private HBoxContainer _updatingAndRestartingHBoxContainer = null!;

	private readonly AutoUpdate _autoUpdate = new();
	private Release? _pendingRelease;
	private string? _pendingArchivePath;
	
	public override void _Ready()
	{
		_lastCheckedAtLabel = GetNode<Label>("%LastCheckedAtLabel");
		_newerVersionLabel = GetNode<Label>("%NewerVersionLabel");
		_checkForUpdatesHBoxContainer = GetNode<HBoxContainer>("%CheckForUpdatesHBoxContainer");
		_downloadUpdateHBoxContainer = GetNode<HBoxContainer>("%DownloadUpdateHBoxContainer");
		_noUpdatesFoundHBoxContainer = GetNode<HBoxContainer>("%NoUpdatesFoundHBoxContainer");
		_checkingForUpdatesHBoxContainer = GetNode<HBoxContainer>("%CheckingForUpdatesHBoxContainer");
		_downloadingUpdateHBoxContainer = GetNode<HBoxContainer>("%DownloadingUpdateHBoxContainer");
		_finishUpdateAndRestartHBoxContainer = GetNode<HBoxContainer>("%FinishUpdateAndRestartHBoxContainer");
		_updatingAndRestartingHBoxContainer = GetNode<HBoxContainer>("%UpdatingAndRestartingHBoxContainer");
		
		_checkForUpdatesButton = GetNode<Button>("%CheckForUpdatesButton");
		_downloadUpdateButton = GetNode<Button>("%DownloadUpdateButton");
		_finishUpdateAndRestartButton = GetNode<Button>("%FinishUpdateAndRestartButton");
		
		_checkForUpdatesButton.Pressed += OnCheckForUpdatesPressed;
		_downloadUpdateButton.Pressed += OnDownloadUpdatePressed;
		_finishUpdateAndRestartButton.Pressed += OnFinishUpdateAndRestartPressed;
		
		UpdateLastCheckedAtLabel();
	}

	private void SetStage(UpdateStage stage)
	{
		_checkForUpdatesHBoxContainer.Visible = stage is UpdateStage.Idle or UpdateStage.NoUpdateFound;
		_checkingForUpdatesHBoxContainer.Visible = stage is UpdateStage.Checking;
		_downloadUpdateHBoxContainer.Visible = stage is UpdateStage.UpdateAvailable;
		_noUpdatesFoundHBoxContainer.Visible = stage is UpdateStage.NoUpdateFound;
		_downloadingUpdateHBoxContainer.Visible = stage is UpdateStage.Downloading;
		_finishUpdateAndRestartHBoxContainer.Visible = stage is UpdateStage.ReadyToInstall;
		_updatingAndRestartingHBoxContainer.Visible = stage is UpdateStage.Installing;
	}

	private async void OnCheckForUpdatesPressed()
	{
		SetStage(UpdateStage.Checking);

		var timer = Stopwatch.StartNew();
		var release = await _autoUpdate.CheckForUpdates(Singletons.AppState.LastCheckedForUpdates);
		timer.Stop();
		if (release is null && timer.Elapsed < TimeSpan.FromSeconds(1))
		{
			var extraTimeToWait = TimeSpan.FromSeconds(1) - timer.Elapsed;
			await Task.Delay(extraTimeToWait);
		}
		
		Singletons.AppState.LastCheckedForUpdates = DateTimeOffset.UtcNow;
		UpdateLastCheckedAtLabel();
		
		if (release is null)
		{
			SetStage(UpdateStage.NoUpdateFound);
			return;
		}

		_pendingRelease = release;
		_newerVersionLabel.Text = $"Newer version: {release.Name}";
		SetStage(UpdateStage.UpdateAvailable);
	}

	private async void OnDownloadUpdatePressed()
	{
		if (_pendingRelease is null) return;
		
		SetStage(UpdateStage.Downloading);
		_pendingArchivePath = await _autoUpdate.EnsureReleaseZipReadyForSwap(_pendingRelease);
		SetStage(UpdateStage.ReadyToInstall);
	}

	private async void OnFinishUpdateAndRestartPressed()
	{
		if (_pendingArchivePath is null) return;
		
		SetStage(UpdateStage.Installing);
		await _autoUpdate.StartUpdaterProcess(_pendingArchivePath);
		GetTree().Quit();
	}

	private void UpdateLastCheckedAtLabel()
	{
		var lastChecked = Singletons.AppState.LastCheckedForUpdates;
		_lastCheckedAtLabel.Text = lastChecked is null
			? "Last checked at: never"
			: $"Last checked at: {lastChecked.Value.ToLocalTime():g}";
	}
}
