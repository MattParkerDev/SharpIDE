
using Godot;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel
{
    private readonly Texture2D _csIcon = ResourceLoader.Load<Texture2D>("uid://do0edciarrnp0");
    private readonly Texture2D _razorIcon = ResourceLoader.Load<Texture2D>("uid://cff7jlvj2tlg2");
    private readonly Texture2D _jsonIcon = ResourceLoader.Load<Texture2D>("uid://csrwpjk77r731");
    private readonly Texture2D _jsIcon = ResourceLoader.Load<Texture2D>("uid://cpdobpjrm2suc");

    private Texture2D GetIconForFileExtension(string fileExtension)
    {
        var texture = fileExtension switch
        {
            ".cs" => _csIcon,
            ".razor" or ".cshtml" => _razorIcon,
            ".json" => _jsonIcon,
            ".js" => _jsIcon,
            _ => _csIcon
        };    
        return texture;
    }
}