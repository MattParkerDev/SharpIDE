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
	private ProgressBar _downloadProgressBar = null!;
	private Label _downloadProgressLabel = null!;
	
	private VBoxContainer _finishUpdateAndRestartVBoxContainer = null!;
	private Button _finishUpdateAndRestartButton = null!;
	private LinkButton _updaterLogLinkButton = null!;
	
	private HBoxContainer _updatingAndRestartingHBoxContainer = null!;
	
	private HBoxContainer _failedHBoxContainer = null!;
	private Label _failedLabel = null!;

	private Release? _pendingRelease;
	private string? _pendingArchivePath;
	
	public override void _Ready()
	{
		_lastCheckedAtLabel = GetNode<Label>("%LastCheckedAtLabel");
		_newerVersionLabel = GetNode<Label>("%NewerVersionLabel");
		_failedLabel = GetNode<Label>("%FailedLabel");
		_downloadProgressLabel = GetNode<Label>("%DownloadProgressLabel");
		_downloadProgressBar = GetNode<ProgressBar>("%DownloadProgressBar");
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
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
			await func();
		}
		catch (Exception ex)
		{
			_downloadProgress = null;
			Callable.From(() =>
			{
				GD.PrintErr($"Auto-update failed: {ex}");
				_failedLabel.Text = $"Failed: {ex.Message}";
				SetStage(UpdateStage.Failed);
			}).CallDeferred();
		}
	}

	private async Task OnCheckForUpdatesPressed()
	{
		await this.InvokeAsync(() => SetStage(UpdateStage.Checking));

		var timer = Stopwatch.StartNew();
		var release = await AutoUpdate.CheckForUpdates(Singletons.AppState.LastCheckedForUpdates);
		timer.Stop();
		if (release is null && timer.Elapsed < TimeSpan.FromSeconds(1))
		{
			var extraTimeToWait = TimeSpan.FromSeconds(1) - timer.Elapsed;
			await Task.Delay(extraTimeToWait);
		}
		
		Singletons.AppState.LastCheckedForUpdates = DateTimeOffset.UtcNow;
		await this.InvokeAsync(() =>
		{
			UpdateLastCheckedAtLabel();
		
			if (release is null)
			{
				SetStage(UpdateStage.NoUpdateFound);
				return;
			}

			_pendingRelease = release;
			_newerVersionLabel.Text = $"Newer version: {release.Name}";
			SetStage(UpdateStage.UpdateAvailable);
		});
	}

	public override void _Process(double delta)
	{
		if (_downloadProgress is not null)
		{
			if (_downloadProgress.TotalBytes is 0) return;
			if (_downloadProgressBar.Indeterminate)
			{
				_downloadProgressBar.Indeterminate = false;
				_downloadProgressBar.Value = 0;
				_downloadProgressBar.MaxValue = _downloadProgress.TotalBytes;
				_downloadProgressLabel.Text = $"Downloading - {_downloadProgress.TotalBytes / (1024.0 * 1024.0):F1}MB total";
			}
			_downloadProgressBar.Value = _downloadProgress.CurrentBytes;
			if (_downloadProgress.CurrentBytes == _downloadProgress.TotalBytes)
			{
				_downloadProgressBar.Indeterminate = true;
				_downloadProgressBar.Value = 0;
				_downloadProgressLabel.Text = "Extracting";
				_downloadProgress = null;
			}
		}
	}

	private DownloadProgress? _downloadProgress;
	private async Task OnDownloadUpdatePressed()
	{
		if (_pendingRelease is null) return;
		await this.InvokeAsync(() => SetStage(UpdateStage.Downloading));
		_downloadProgress = new DownloadProgress();
		_pendingArchivePath = await AutoUpdate.EnsureReleaseZipReadyForSwap(_pendingRelease, _downloadProgress);
		await this.InvokeAsync(() => SetStage(UpdateStage.ReadyToInstall));
	}

	private async Task OnFinishUpdateAndRestartPressed()
	{
		if (_pendingArchivePath is null) return;
		
		await this.InvokeAsync(() => SetStage(UpdateStage.Installing));
		await AutoUpdate.StartUpdaterProcess(_pendingArchivePath);
		await this.InvokeAsync(() => GetTree().Quit());
	}

	private void UpdateLastCheckedAtLabel()
	{
		var lastChecked = Singletons.AppState.LastCheckedForUpdates;
		_lastCheckedAtLabel.Text = lastChecked is null
			? "Last checked at: never"
			: $"Last checked at: {lastChecked.Value.ToLocalTime():g}";
	}
}

public class DownloadProgress : IProgress<(long currentBytes, long totalBytes)>
{
	public long CurrentBytes;
	public long TotalBytes;

	public void Report((long currentBytes, long totalBytes) s)
	{
		CurrentBytes = s.currentBytes;
		TotalBytes = s.totalBytes;
	}
}