using Godot;

namespace SharpIDE.Godot.Features.Nuget;

public enum PackageChangeKind { Install, Upgrade, Downgrade, Remove }

public sealed record PackageChangeDialogRow(PackageDetailsProjectEntry Entry, string OldVersion, string NewVersion);

/// Confirms a change of a package across several projects, listing each with its old and new version - any can be unticked to leave it out.
/// Frees itself once closed.
public partial class PackageChangeDialog : ConfirmationDialog
{
	private const int RowHeight = 28;

	private readonly Tree _tree = new();
	private readonly List<(TreeItem Item, PackageChangeDialogRow Row)> _rows = [];

	/// The rows still ticked when the change was confirmed
	public event Action<List<PackageChangeDialogRow>>? RowsConfirmed;

	/// Call once, before adding the dialog to the tree
	public void Setup(string title, string okButtonText, string packageId, IReadOnlyList<PackageChangeDialogRow> rows)
	{
		Title = title;
		OkButtonText = okButtonText;
		Exclusive = true;
		Transient = true;

		_tree.Columns = 5;
		_tree.ColumnTitlesVisible = true;
		_tree.HideRoot = true;
		_tree.SelectMode = Tree.SelectModeEnum.Row;
		_tree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		string[] titles = ["", "Project", "Package", "Old version", "New version"];
		foreach (var (column, columnTitle) in titles.Index())
		{
			_tree.SetColumnTitle(column, columnTitle);
			_tree.SetColumnTitleAlignment(column, HorizontalAlignment.Left);
			_tree.SetColumnClipContent(column, true);
		}
		_tree.SetColumnExpand(0, false);
		_tree.SetColumnCustomMinimumWidth(0, 32);
		_tree.SetColumnExpandRatio(1, 3);
		_tree.SetColumnExpandRatio(2, 3);
		_tree.SetColumnExpandRatio(3, 2);
		_tree.SetColumnExpandRatio(4, 2);

		var root = _tree.CreateItem();
		foreach (var row in rows)
		{
			var item = _tree.CreateItem(root);
			item.SetCellMode(0, TreeItem.TreeCellMode.Check);
			item.SetEditable(0, true);
			item.SetChecked(0, true);
			item.SetText(1, row.Entry.ProjectModel.Name.Value);
			item.SetTooltipText(1, row.Entry.ProjectModel.FilePath);
			item.SetText(2, packageId);
			item.SetText(3, row.OldVersion);
			item.SetText(4, row.NewVersion);
			_rows.Add((item, row));
		}
		AddChild(_tree);

		// Double-clicking (or Enter on) a row ticks or unticks it, as well as its checkbox
		_tree.ItemActivated += () =>
		{
			if (_tree.GetSelected() is not { } selected) return;
			selected.SetChecked(0, selected.IsChecked(0) is false);
			UpdateOkButton();
		};
		_tree.ItemEdited += UpdateOkButton;
		Confirmed += () => RowsConfirmed?.Invoke(_rows.Where(r => r.Item.IsChecked(0)).Select(r => r.Row).ToList());
		VisibilityChanged += () =>
		{
			if (Visible is false) QueueFree();
		};
	}

	/// Shows the dialog centred on its parent's window, sized to its rows
	public void PopupForRows()
	{
		var height = Math.Clamp(110 + (_rows.Count + 1) * RowHeight, 200, 600);
		PopupCentered(new Vector2I(820, height));
		GetOkButton().GrabFocus();
	}

	private void UpdateOkButton() => GetOkButton().Disabled = _rows.Any(r => r.Item.IsChecked(0)) is false;
}
