using Godot;
using SharpIDE.Application.Features.Analysis;

namespace SharpIDE.Godot.Features.CodeEditor;

/// Exclusive, so blocks all input to the IDE while open. Frees itself when closed.
public partial class CodeActionProgressWindow : Window
{
	private readonly Label _descriptionLabel = new();
	private readonly ProgressBar _progressBar = new();
	private readonly Label _countLabel = new();
	private readonly Button _cancelButton = new();
	private CodeActionProgress _progress = null!;
	private CancellationTokenSource _cancellationTokenSource = null!;
	private CodeActionProgressSnapshot? _lastSnapshot;

	public void Setup(string title, CodeActionProgress progress, CancellationTokenSource cancellationTokenSource)
	{
		_progress = progress;
		_cancellationTokenSource = cancellationTokenSource;
		Title = title;
		Exclusive = true;
		Transient = true;
		Unresizable = true;
		WrapControls = true;

		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var margin = new MarginContainer();
		foreach (var side in (string[])["left", "right", "top", "bottom"]) margin.AddThemeConstantOverride($"margin_{side}", 16);
		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("separation", 10);

		_descriptionLabel.Text = "Computing fix all occurrences...";
		_descriptionLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		_descriptionLabel.CustomMinimumSize = new Vector2(440, 0);
		_progressBar.CustomMinimumSize = new Vector2(0, 20);
		_progressBar.Indeterminate = true;
		_progressBar.ShowPercentage = false;
		_countLabel.HorizontalAlignment = HorizontalAlignment.Right;

		_cancelButton.Text = "Cancel";
		_cancelButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
		_cancelButton.Pressed += RequestCancel;
		CloseRequested += RequestCancel;

		vbox.AddChild(_descriptionLabel);
		vbox.AddChild(_progressBar);
		vbox.AddChild(_countLabel);
		vbox.AddChild(_cancelButton);
		margin.AddChild(vbox);
		panel.AddChild(margin);
		AddChild(panel);
	}

	public override void _Ready()
	{
		PopupCentered(new Vector2I(480, 150));
		_cancelButton.GrabFocus();
	}

	// Polled, as reports come from Roslyn's threads, often many per frame
	public override void _Process(double delta)
	{
		var snapshot = _progress.Snapshot;
		if (snapshot == _lastSnapshot) return;
		_lastSnapshot = snapshot;
		if (snapshot.Description is { } description) _descriptionLabel.Text = description;
		// Cancelling now would leave the changes half applied
		if (snapshot.IsApplying) _cancelButton.Disabled = true;
		if (snapshot.Total is 0)
		{
			_progressBar.Indeterminate = true;
			_countLabel.Text = string.Empty;
		}
		else
		{
			_progressBar.Indeterminate = false;
			_progressBar.MaxValue = snapshot.Total;
			_progressBar.Value = snapshot.Completed;
			_countLabel.Text = $"{snapshot.Completed} / {snapshot.Total}";
		}
	}

	public void Close()
	{
		Hide();
		QueueFree();
	}

	private void RequestCancel()
	{
		if (_cancelButton.Disabled) return;
		_cancelButton.Disabled = true;
		_cancelButton.Text = "Cancelling...";
		_cancellationTokenSource.Cancel();
	}
}
