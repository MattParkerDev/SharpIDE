using System.Diagnostics;
using Godot;
using Octokit;
using SharpIDE.Godot.Features.IdeAutoUpdate;
using Environment = System.Environment;
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
	
	private VBoxContainer _finishUpdateAndRestartVBoxContainer = null!;
	private Button _finishUpdateAndRestartButton = null!;
	private LinkButton _updaterLogLinkButton = null!;
	
	private HBoxContainer _updatingAndRestartingHBoxContainer = null!;
	
	private HBoxContainer _failedHBoxContainer = null!;
	private Label _failedLabel = null!;

	private readonly AutoUpdate _autoUpdate = new();
	private Release? _pendingRelease;
	private string? _pendingArchivePath;
	
	public override void _Ready()
	{
		_lastCheckedAtLabel = GetNode<Label>("%LastCheckedAtLabel");
		_newerVersionLabel = GetNode<Label>("%NewerVersionLabel");
		_failedLabel = GetNode<Label>("%FailedLabel");
		_checkForUpdatesHBoxContainer = GetNode<HBoxContainer>("%CheckForUpdatesHBoxContainer");
		_downloadUpdateHBoxContainer = GetNode<HBoxContainer>("%DownloadUpdateHBoxContainer");
		_noUpdatesFoundHBoxContainer = GetNode<HBoxContainer>("%NoUpdatesFoundHBoxContainer");
		_checkingForUpdatesHBoxContainer = GetNode<HBoxContainer>("%CheckingForUpdatesHBoxContainer");
		_downloadingUpdateHBoxContainer = GetNode<HBoxContainer>("%DownloadingUpdateHBoxContainer");
		_finishUpdateAndRestartVBoxContainer = GetNode<VBoxContainer>("%FinishUpdateAndRestartVBoxContainer");
		_updatingAndRestartingHBoxContainer = GetNode<HBoxContainer>("%UpdatingAndRestartingHBoxContainer");
		_failedHBoxContainer = GetNode<HBoxContainer>("%FailedHBoxContainer");
		
		_checkForUpdatesButton = GetNode<Button>("%CheckForUpdatesButton");
		_downloadUpdateButton = GetNode<Button>("%DownloadUpdateButton");
		_finishUpdateAndRestartButton = GetNode<Button>("%FinishUpdateAndRestartButton");
		_updaterLogLinkButton = GetNode<LinkButton>("%UpdaterLogLinkButton");

		_checkForUpdatesButton.Pressed += () => WithFailureGuard(OnCheckForUpdatesPressed);
		_downloadUpdateButton.Pressed += () => WithFailureGuard(OnDownloadUpdatePressed);
		_finishUpdateAndRestartButton.Pressed += () => WithFailureGuard(OnFinishUpdateAndRestartPressed);
		_updaterLogLinkButton.Pressed += () =>
		{
			var logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharpIDE", "update-log.txt");
			if (File.Exists(logFilePath) is false) File.OpenWrite(logFilePath).Dispose();
			OS.ShellShowInFileManager(logFilePath);
		};
		
		UpdateLastCheckedAtLabel();
	}

	private void SetStage(UpdateStage stage)
	{
		_checkForUpdatesHBoxContainer.Visible = stage is UpdateStage.Idle or UpdateStage.NoUpdateFound or UpdateStage.Failed;
		_checkingForUpdatesHBoxContainer.Visible = stage is UpdateStage.Checking;
		_downloadUpdateHBoxContainer.Visible = stage is UpdateStage.UpdateAvailable;
		_noUpdatesFoundHBoxContainer.Visible = stage is UpdateStage.NoUpdateFound;
		_downloadingUpdateHBoxContainer.Visible = stage is UpdateStage.Downloading;
		_finishUpdateAndRestartVBoxContainer.Visible = stage is UpdateStage.ReadyToInstall;
		_updatingAndRestartingHBoxContainer.Visible = stage is UpdateStage.Installing;
		_failedHBoxContainer.Visible = stage is UpdateStage.Failed;
	}

	private async void WithFailureGuard(Func<Task> func)
	{
		try
		{
			await func();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"Auto-update failed: {ex}");
			_failedLabel.Text = $"Failed: {ex.Message}";
			SetStage(UpdateStage.Failed);
		}
	}

	private async Task OnCheckForUpdatesPressed()
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

	private async Task OnDownloadUpdatePressed()
	{
		if (_pendingRelease is null) return;
		
		SetStage(UpdateStage.Downloading);
		_pendingArchivePath = await _autoUpdate.EnsureReleaseZipReadyForSwap(_pendingRelease);
		SetStage(UpdateStage.ReadyToInstall);
	}

	private async Task OnFinishUpdateAndRestartPressed()
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
