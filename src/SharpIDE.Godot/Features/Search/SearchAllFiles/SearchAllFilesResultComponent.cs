using Godot;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Search;
using SharpIDE.Godot.Features.SolutionExplorer;

namespace SharpIDE.Godot.Features.Search.SearchAllFiles;

public partial class SearchAllFilesResultComponent : MarginContainer
{
    private TextureRect _textureRect = null!;
    private TextureRect _overlayTextureRect = null!;
    private Label _fileNameLabel = null!;
    private Label _filePathLabel = null!;
    private Button _button = null!;
    
    private Texture2D _csharpFileIcon = ResourceLoader.Load<Texture2D>("uid://do0edciarrnp0");
    private Texture2D _folderIcon = ResourceLoader.Load<Texture2D>("uid://xc8srvqwlwng");
    
    public SearchAllFilesWindow ParentSearchAllFilesWindow { get; set; } = null!;
    public FindFilesSearchResult Result { get; set; } = null!;
    
    public override void _Ready()
    {
        _button = GetNode<Button>("Button");
        _textureRect = GetNode<TextureRect>("%IconTextureRect");
        _overlayTextureRect = GetNode<TextureRect>("%IconOverlayTextureRect");
        _fileNameLabel = GetNode<Label>("%FileNameLabel");
        _filePathLabel = GetNode<Label>("%FilePathLabel");
        SetValue(Result);
        _button.Pressed += OnButtonPressed;
    }

    private void OnButtonPressed()
    {
        GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(Result.File, null);
        ParentSearchAllFilesWindow.Hide();
    }

    private void SetValue(FindFilesSearchResult result)
    {
        if (result is null) return;
        var (icon, overlayIcon) = FileIconHelper.GetIconForFileExtension(result.File.Extension);
        _textureRect.Texture = icon;
        _overlayTextureRect.Texture = overlayIcon;
        _fileNameLabel.Text = result.File.Name.Value;
        _filePathLabel.Text = result.File.Path;
    }
}