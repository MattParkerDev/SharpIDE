using Godot;

namespace SharpIDE.Godot.Features.Settings;

public partial class FontPickerDialog : Window
{
	// Godot Signals don't support nullable value types, e.g. 'int?'
	[Signal]
	public delegate void FontSelectedEventHandler(FontPickerResult result);
	
	private ItemList _systemFontItemList = null!;
	private ItemList _fontSizeItemList = null!;
	private CodeEdit _previewCodeEdit = null!;
	private Button _resetToDefaultButton = null!;
	private Button _saveButton = null!;
	private Button _cancelButton = null!;

	public Font DefaultFont { get; set; } = null!;
	public int DefaultFontSize { get; set; } = -1;

	public string? CurrentSystemFontName { get; set; }
	public int? CurrentFontSize { get; set; }

	private string? _selectedSystemFontName;
	private int? _selectedFontSize;

	public override void _Ready()
	{
		_systemFontItemList = GetNode<ItemList>("%SystemFontItemList");
		_fontSizeItemList = GetNode<ItemList>("%FontSizeItemList");
		_previewCodeEdit = GetNode<CodeEdit>("%PreviewCodeEdit");
		_resetToDefaultButton = GetNode<Button>("%ResetToDefaultButton");
		_saveButton = GetNode<Button>("%SaveButton");
		_cancelButton = GetNode<Button>("%CancelButton");

		CloseRequested += QueueFree;
		_systemFontItemList.ItemSelected += OnSystemFontItemListItemSelected;
		_fontSizeItemList.ItemSelected += OnFontSizeItemListItemSelected;
		_resetToDefaultButton.Pressed += OnResetToDefaultButtonPressed;
		_saveButton.Pressed += OnSaveButtonPressed;
		_cancelButton.Pressed += QueueFree;

		PopulateFontList(CurrentSystemFontName);
		SetInitialFontSize(CurrentFontSize);
	}

	private void PopulateFontList(string? currentSystemFontName)
	{
		_systemFontItemList.Clear();
		var systemFontNames = OS.GetSystemFonts();
		if (systemFontNames.Contains(currentSystemFontName) is false) currentSystemFontName = null;
		_selectedSystemFontName = currentSystemFontName;

		_systemFontItemList.AddItem($"Default - {DefaultFont.GetFontName()}");
		_systemFontItemList.Select(0);
		foreach (var fontName in systemFontNames.Order())
		{
			_systemFontItemList.AddItem(fontName);
			if (fontName == _selectedSystemFontName)
			{
				_systemFontItemList.Select(_systemFontItemList.GetItemCount() - 1);
			}
		}
		_systemFontItemList.EnsureCurrentIsVisible();

		var font = _selectedSystemFontName is null ? DefaultFont : new SystemFont { FontNames = [_selectedSystemFontName] };
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, font);
	}

	private void SetInitialFontSize(int? fontSize)
	{
		_fontSizeItemList.SetItemText(0, $"Default - {DefaultFontSize}");
		if (fontSize is null)
		{
			_fontSizeItemList.Select(0);
			_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, DefaultFontSize);
			return;
		}
		var currentSize = fontSize.ToString();
		for (var i = 0; i < _fontSizeItemList.GetItemCount(); i++)
		{
			if (_fontSizeItemList.GetItemText(i) != currentSize) continue;
			_fontSizeItemList.Select(i);
			break;
		}

		_fontSizeItemList.EnsureCurrentIsVisible();
		_selectedFontSize = fontSize.Value;
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, fontSize.Value);
	}

	private void OnSystemFontItemListItemSelected(long index)
	{
		if (index is 0)
		{
			_selectedSystemFontName = null;
			_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, DefaultFont);
			return;
		}
		var systemFontName = _systemFontItemList.GetItemText((int)index);
		_selectedSystemFontName = systemFontName;
		var font = new SystemFont { FontNames = [_selectedSystemFontName] };
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, font);
	}

	private void OnFontSizeItemListItemSelected(long index)
	{
		if (index is 0)
		{
			_selectedFontSize = null;
			_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, DefaultFontSize);
			return;
		}
		var px = _fontSizeItemList.GetItemText((int)index).ToInt();
		_selectedFontSize = px;
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, _selectedFontSize.Value);
	}

	private void OnResetToDefaultButtonPressed()
	{
		_selectedSystemFontName = null;
		_selectedFontSize = null;
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, DefaultFont);
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, DefaultFontSize);
		_systemFontItemList.Select(0);
		_fontSizeItemList.Select(0);
		
		_systemFontItemList.EnsureCurrentIsVisible();
		_fontSizeItemList.EnsureCurrentIsVisible();
	}

	private void OnSaveButtonPressed()
	{
		EmitSignalFontSelected(new FontPickerResult(_selectedSystemFontName, _selectedFontSize));
		QueueFree();
	}
}

public partial class FontPickerResult(string? systemFontName, int? fontSize) : GodotObject
{
	public string? SystemFontName { get; init; } = systemFontName;
	public int? FontSize { get; init; } = fontSize;

	public void Deconstruct(out string? systemFontName, out int? fontSize)
	{
		systemFontName = SystemFontName;
		fontSize = FontSize;
	}
}