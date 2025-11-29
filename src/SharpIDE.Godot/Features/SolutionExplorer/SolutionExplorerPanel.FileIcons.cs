
using Godot;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel
{
    private readonly Texture2D _csIcon = ResourceLoader.Load<Texture2D>("uid://do0edciarrnp0");
    private readonly Texture2D _razorIcon = ResourceLoader.Load<Texture2D>("uid://cff7jlvj2tlg2");
    private readonly Texture2D _jsonIcon = ResourceLoader.Load<Texture2D>("uid://csrwpjk77r731");
    private readonly Texture2D _jsIcon = ResourceLoader.Load<Texture2D>("uid://cpdobpjrm2suc");
    private readonly Texture2D _htmlIcon = ResourceLoader.Load<Texture2D>("uid://q0cktiwdkt1e");
    private readonly Texture2D _cssIcon = ResourceLoader.Load<Texture2D>("uid://b6m4rm5u8hd1c");
    private readonly Texture2D _txtIcon = ResourceLoader.Load<Texture2D>("uid://b6bpjhs2o1j2l");
    private readonly Texture2D _genericFileIcon = ResourceLoader.Load<Texture2D>("uid://bile1h6sq0l08");
    private readonly Texture2D _mdFileIcon = ResourceLoader.Load<Texture2D>("uid://8i2y6xjdjno3");
    private readonly Texture2D _editorConfigFileIcon = ResourceLoader.Load<Texture2D>("uid://5t83l7c7f3g6");
    
    private readonly Texture2D _propsFileOverlayIcon = ResourceLoader.Load<Texture2D>("uid://fa7tdmldi206");

    private (Texture2D Icon, Texture2D? OverlayIcon) GetIconForFileExtension(string fileExtension)
    {
        var texture = fileExtension switch
        {
            ".cs" => _csIcon,
            ".razor" or ".cshtml" => _razorIcon,
            ".json" => _jsonIcon,
            ".js" => _jsIcon,
            ".html" or ".htm" => _htmlIcon,
            ".css" => _cssIcon,
            ".txt" => _txtIcon,
            ".props" => _genericFileIcon,
            ".md" => _mdFileIcon,
            ".editorconfig" => _editorConfigFileIcon,
            _ => _csIcon
        };
        var overlayTexture = fileExtension switch
        {
            ".props" => _propsFileOverlayIcon,
            _ => null
        };
        
        return (texture, overlayTexture);
    }
}