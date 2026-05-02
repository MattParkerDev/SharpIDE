using Godot;

namespace SharpIDE.Godot.Features.Settings;

public partial class FontPickerDialog : Window
{
	[Signal]
	public delegate void FontSelectedEventHandler(string font, int fontSize);
	
	private ItemList _fontList = null!;
	private ItemList _fontSize = null!;
	private CodeEdit _preview = null!;
	private Button _resetToDefaultButton = null!;
	private Button _apply = null!;
	private Button _cancel = null!;

	private string _selectedFont = "res://Features/CodeEditor/Resources/CascadiaFontVariation.tres";
	private int _selectedSize = 18;

	public override void _Ready()
	{
		_fontList = GetNode<ItemList>("%FontList");
		_fontSize = GetNode<ItemList>("%FontSize");
		_preview = GetNode<CodeEdit>("%Preview");
		_resetToDefaultButton = GetNode<Button>("%ResetToDefaultButton");
		_apply = GetNode<Button>("%Apply");
		_cancel = GetNode<Button>("%Cancel");

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
		var fonts = OS.GetSystemFonts().ToList();
		var currentFont = Singletons.AppState.IdeSettings.EditorFont;
		if (currentFont.StartsWith("res://"))
			currentFont = "Default Font";
		fonts.Add("Default Font");
		foreach (var font in fonts.Order())
		{
			var i = _fontList.GetItemCount();
			_fontList.AddItem(font);
			if (currentFont == font)
			{
				_fontList.Select(i);
				_selectedFont = font;
			}
		}

		Callable.From(() => _fontList.EnsureCurrentIsVisible()).CallDeferred();
		if (currentFont == "Default Font") return;
		var nfont = new SystemFont()
		{
			FontNames = [currentFont]
		};
		_preview.AddThemeFontOverride("font", nfont);
	}

	private void UpdateFontSize()
	{
		var currentSize = $"{Singletons.AppState.IdeSettings.FontSize}";
		for (var i = 0; i < _fontSize.GetItemCount(); i++)
		{
			if (_fontSize.GetItemText(i) != currentSize) continue;
			_fontSize.Select(i);
			break;
		}

		Callable.From(() => _fontSize.EnsureCurrentIsVisible()).CallDeferred();
		_selectedSize = Singletons.AppState.IdeSettings.FontSize;
		_preview.AddThemeFontSizeOverride("font_size", Singletons.AppState.IdeSettings.FontSize);
	}

	private void OnFontListItemSelected(long index)
	{
		var font = _fontList.GetItemText((int)index);
		_selectedFont = font == "Default Font" ? "res://Features/CodeEditor/Resources/CascadiaFontVariation.tres" : font;
		if (_selectedFont.StartsWith("res://"))
		{
			_preview.AddThemeFontOverride("font", GD.Load<FontVariation>("res://Features/CodeEditor/Resources/CascadiaFontVariation.tres"));
		}
		else
		{
			var nfont = new SystemFont();
			nfont.FontNames = [_selectedFont];
			_preview.AddThemeFontOverride("font", nfont);
		}
	}

	private void OnFontSizeItemSelected(long index)
	{
		var points = _fontSize.GetItemText((int)index).ToInt();
		_selectedSize = points;
		_preview.AddThemeFontSizeOverride("font_size", _selectedSize);
	}

	private void OnResetToDefaultButtonPressed()
	{
		_selectedFont = "res://Features/CodeEditor/Resources/CascadiaFontVariation.tres";
		_selectedSize = 18;
		_preview.AddThemeFontOverride("font", GD.Load<FontVariation>("res://Features/CodeEditor/Resources/CascadiaFontVariation.tres"));
		_preview.AddThemeFontSizeOverride("font_size", 18);
		for (var i = 0; i < _fontList.GetItemCount(); i++)
		{
			if (_fontList.GetItemText(i) != "Default Font") continue;
			_fontList.Select(i);
			break;
		}

		for (var i = 0; i < _fontSize.GetItemCount(); i++)
		{
			if (_fontSize.GetItemText(i) != $"{_selectedSize}") continue;
			_fontSize.Select(i);
			break;
		}
		Callable.From(() =>
		{
			_fontList.EnsureCurrentIsVisible();
			_fontSize.EnsureCurrentIsVisible();
		}).CallDeferred();
	}

	private void OnApplyPressed()
	{
		EmitSignalFontSelected(_selectedFont, _selectedSize);
		QueueFree();
	}
}
