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
	
	private Font _editorDefaultFont = null!;
	private int _editorDefaultFontSize = -1;

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
		
		_editorDefaultFont = GetThemeFont(ThemeStringNames.Font, GodotNodeStringNames.CodeEdit);
		_editorDefaultFontSize = GetThemeFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.CodeEdit);

		CloseRequested += QueueFree;
		_systemFontItemList.ItemSelected += OnSystemFontItemListItemSelected;
		_fontSizeItemList.ItemSelected += OnFontSizeItemListItemSelected;
		_resetToDefaultButton.Pressed += OnResetToDefaultButtonPressed;
		_saveButton.Pressed += OnSaveButtonPressed;
		_cancelButton.Pressed += QueueFree;

		PopulateFontList();
		UpdateFontSize();
	}

	private void PopulateFontList()
	{
		_systemFontItemList.Clear();
		var systemFontNames = OS.GetSystemFonts();
		if (systemFontNames.Contains(Singletons.AppState.IdeSettings.EditorSystemFontName) is false) Singletons.AppState.IdeSettings.EditorSystemFontName = null;
		_selectedSystemFontName = Singletons.AppState.IdeSettings.EditorSystemFontName;

		_systemFontItemList.AddItem($"SharpIDE Default - {_editorDefaultFont.GetFontName()}");
		_systemFontItemList.Select(0);
		foreach (var fontName in systemFontNames)
		{
			_systemFontItemList.AddItem(fontName);
			if (fontName == _selectedSystemFontName)
			{
				_systemFontItemList.Select(_systemFontItemList.GetItemCount() - 1);
			}
		}
		_systemFontItemList.EnsureCurrentIsVisible();
		
		if (_selectedSystemFontName is null) return;
		var font = new SystemFont { FontNames = [_selectedSystemFontName] };
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, font);
	}

	private void UpdateFontSize()
	{
		if (Singletons.AppState.IdeSettings.EditorFontSize is null)
		{
			_fontSizeItemList.Select(0);
			return;
		}
		var currentSize = Singletons.AppState.IdeSettings.EditorFontSize.ToString();
		for (var i = 0; i < _fontSizeItemList.GetItemCount(); i++)
		{
			if (_fontSizeItemList.GetItemText(i) != currentSize) continue;
			_fontSizeItemList.Select(i);
			break;
		}

		_fontSizeItemList.EnsureCurrentIsVisible();
		_selectedFontSize = Singletons.AppState.IdeSettings.EditorFontSize.Value;
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, Singletons.AppState.IdeSettings.EditorFontSize.Value);
	}

	private void OnSystemFontItemListItemSelected(long index)
	{
		if (index is 0)
		{
			_selectedSystemFontName = null;
			_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, _editorDefaultFont);
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
			_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, _editorDefaultFontSize);
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
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, _editorDefaultFont);
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, _editorDefaultFontSize);
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