using Godot;

namespace SharpIDE.Godot.Features.Settings;

public partial class FontPickerDialog : Window
{
	// Godot Signals don't support nullable value types, e.g. 'int?'
	[Signal]
	public delegate void FontSelectedEventHandler(FontPickerResult result);
	
	private ItemList _fontList = null!;
	private ItemList _fontSize = null!;
	private CodeEdit _previewCodeEdit = null!;
	private Button _resetToDefaultButton = null!;
	private Button _apply = null!;
	private Button _cancel = null!;
	
	private Font _editorDefaultFont = null!;
	private int _editorDefaultFontSize = -1;

	private string? _selectedSystemFontName;
	private int? _selectedFontSize;

	public override void _Ready()
	{
		_fontList = GetNode<ItemList>("%FontList");
		_fontSize = GetNode<ItemList>("%FontSize");
		_previewCodeEdit = GetNode<CodeEdit>("%Preview");
		_resetToDefaultButton = GetNode<Button>("%ResetToDefaultButton");
		_apply = GetNode<Button>("%Apply");
		_cancel = GetNode<Button>("%Cancel");
		
		_editorDefaultFont = GetThemeFont(ThemeStringNames.Font, GodotNodeStringNames.CodeEdit);
		_editorDefaultFontSize = GetThemeFontSize(ThemeStringNames.FontSize, GodotNodeStringNames.CodeEdit);

		CloseRequested += QueueFree;
		_fontList.ItemSelected += OnFontListItemSelected;
		_fontSize.ItemSelected += OnFontSizeItemSelected;
		_resetToDefaultButton.Pressed += OnResetToDefaultButtonPressed;
		_apply.Pressed += OnApplyPressed;
		_cancel.Pressed += QueueFree;

		PopulateFontList();
		UpdateFontSize();
	}

	private void PopulateFontList()
	{
		_fontList.Clear();
		var systemFontNames = OS.GetSystemFonts();
		if (systemFontNames.Contains(Singletons.AppState.IdeSettings.EditorFont) is false) Singletons.AppState.IdeSettings.EditorFont = null;
		_selectedSystemFontName = Singletons.AppState.IdeSettings.EditorFont;

		_fontList.AddItem($"SharpIDE Default - {_editorDefaultFont.GetFontName()}", null);
		_fontList.Select(0);
		foreach (var fontName in systemFontNames)
		{
			_fontList.AddItem(fontName);
			if (fontName == _selectedSystemFontName)
			{
				_fontList.Select(_fontList.GetItemCount() - 1);
			}
		}
		_fontList.EnsureCurrentIsVisible();
		
		if (_selectedSystemFontName is null) return;
		var font = new SystemFont { FontNames = [_selectedSystemFontName] };
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, font);
	}

	private void UpdateFontSize()
	{
		if (Singletons.AppState.IdeSettings.FontSize is null)
		{
			_fontSize.Select(0);
			return;
		}
		var currentSize = Singletons.AppState.IdeSettings.FontSize.ToString();
		for (var i = 0; i < _fontSize.GetItemCount(); i++)
		{
			if (_fontSize.GetItemText(i) != currentSize) continue;
			_fontSize.Select(i);
			break;
		}

		_fontSize.EnsureCurrentIsVisible();
		_selectedFontSize = Singletons.AppState.IdeSettings.FontSize.Value;
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, Singletons.AppState.IdeSettings.FontSize.Value);
	}

	private void OnFontListItemSelected(long index)
	{
		if (index is 0)
		{
			_selectedSystemFontName = null;
			_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, _editorDefaultFont);
			return;
		}
		var systemFontName = _fontList.GetItemText((int)index);
		_selectedSystemFontName = systemFontName;
		var font = new SystemFont { FontNames = [_selectedSystemFontName] };
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, font);
	}

	private void OnFontSizeItemSelected(long index)
	{
		if (index is 0)
		{
			_selectedFontSize = null;
			_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, _editorDefaultFontSize);
			return;
		}
		var px = _fontSize.GetItemText((int)index).ToInt();
		_selectedFontSize = px;
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, _selectedFontSize.Value);
	}

	private void OnResetToDefaultButtonPressed()
	{
		_selectedSystemFontName = null;
		_selectedFontSize = null;
		_previewCodeEdit.AddThemeFontOverride(ThemeStringNames.Font, _editorDefaultFont);
		_previewCodeEdit.AddThemeFontSizeOverride(ThemeStringNames.FontSize, _editorDefaultFontSize);
		_fontList.Select(0);
		_fontSize.Select(0);
		
		_fontList.EnsureCurrentIsVisible();
		_fontSize.EnsureCurrentIsVisible();
	}

	private void OnApplyPressed()
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