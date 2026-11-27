using Godot;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public static class FileIconHelper
{
	private static readonly Texture2D _warningIcon = ResourceLoader.Load<Texture2D>("uid://pd3h5qfjn8pb");

	private static readonly Texture2D _csIcon = ResourceLoader.Load<Texture2D>("uid://do0edciarrnp0");
	private static readonly Texture2D _razorIcon = ResourceLoader.Load<Texture2D>("uid://cff7jlvj2tlg2");
	private static readonly Texture2D _jsonIcon = ResourceLoader.Load<Texture2D>("uid://csrwpjk77r731");
	private static readonly Texture2D _jsIcon = ResourceLoader.Load<Texture2D>("uid://cpdobpjrm2suc");
	private static readonly Texture2D _htmlIcon = ResourceLoader.Load<Texture2D>("uid://q0cktiwdkt1e");
	private static readonly Texture2D _cssIcon = ResourceLoader.Load<Texture2D>("uid://b6m4rm5u8hd1c");
	private static readonly Texture2D _txtIcon = ResourceLoader.Load<Texture2D>("uid://b6bpjhs2o1j2l");
	private static readonly Texture2D _genericFileIcon = ResourceLoader.Load<Texture2D>("uid://bile1h6sq0l08");
	private static readonly Texture2D _mdFileIcon = ResourceLoader.Load<Texture2D>("uid://8i2y6xjdjno3");
	private static readonly Texture2D _editorConfigFileIcon = ResourceLoader.Load<Texture2D>("uid://5t83l7c7f3g6");
	private static readonly Texture2D _gitignoreFileIcon = ResourceLoader.Load<Texture2D>("uid://qhtsnkua67ds");
	private static readonly Texture2D _imageFileIcon = ResourceLoader.Load<Texture2D>("uid://73nvtvnx1tie");
	private static readonly Texture2D _fSharpIcon = ResourceLoader.Load<Texture2D>("uid://xa0ntrn7vvbr");
	private static readonly Texture2D _xmlIcon = ResourceLoader.Load<Texture2D>("uid://p5jl3uv51irw");
	private static readonly Texture2D _fontIcon = ResourceLoader.Load<Texture2D>("uid://cuaholbwneg83");
	private static readonly Texture2D _uidIcon = ResourceLoader.Load<Texture2D>("uid://cng3x2drhiicu");
	private static readonly Texture2D _importIcon = ResourceLoader.Load<Texture2D>("uid://dsnos0d8555u8");
	private static readonly Texture2D _godotProjectIcon = ResourceLoader.Load<Texture2D>("uid://cq6wprl6dkf83");
	private static readonly Texture2D _gdScriptIcon = ResourceLoader.Load<Texture2D>("uid://cnpuce0351gn5");
	private static readonly Texture2D _resIcon = ResourceLoader.Load<Texture2D>("uid://chyk4k3f53g28");
	private static readonly Texture2D _tresIcon = ResourceLoader.Load<Texture2D>("uid://dn1h1vf1kfqfk");
	private static readonly Texture2D _scnIcon = ResourceLoader.Load<Texture2D>("uid://dq8fsp1yrmlao");
	private static readonly Texture2D _tscnIcon = ResourceLoader.Load<Texture2D>("uid://burx1v4x47omc");
	private static readonly Texture2D _gdshaderIcon = ResourceLoader.Load<Texture2D>("uid://bd1qmh8tqfx3e");
	private static readonly Texture2D _gdshaderincIcon = ResourceLoader.Load<Texture2D>("uid://cyxwhh12jffqn");
	public static readonly Texture2D CsprojIcon = ResourceLoader.Load<Texture2D>("uid://cqt30ma6xgder");

	private static readonly Texture2D _propsFileOverlayIcon = ResourceLoader.Load<Texture2D>("uid://fa7tdmldi206");
	private static readonly Texture2D _configFileOverlayIcon = ResourceLoader.Load<Texture2D>("uid://brsdisqgeah5n");
	private static readonly Texture2D _targetsFileOverlayIcon = ResourceLoader.Load<Texture2D>("uid://xy5ad1lc24lv");
	private static readonly Texture2D _unknownFileOverlayIcon = ResourceLoader.Load<Texture2D>("uid://7rqmyeiy4y82");

	public static Texture2D WarningIcon => _warningIcon;
	public static (Texture2D Icon, Texture2D? OverlayIcon) GetIconForFileExtension(string fileExtension)
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
			".props" or ".config" or ".targets" or ".resx" => _genericFileIcon,
			".md" => _mdFileIcon,
			".editorconfig" => _editorConfigFileIcon,
			".gitignore" => _gitignoreFileIcon,
			".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".ico" or ".avif" or ".webp" => _imageFileIcon,
			".fs" => _fSharpIcon,
			".xml" or ".nuspec" => _xmlIcon,
			".ttf" or ".otf" or ".woff" or ".woff2" or ".ttc" => _fontIcon,
			".uid" => _uidIcon,
			".import" => _importIcon,
			".godot" => _godotProjectIcon,
			".gd" => _gdScriptIcon,
			".res" => _resIcon,
			".tres" => _tresIcon,
			".scn" => _scnIcon,
			".tscn" => _tscnIcon,
			".gdshader" => _gdshaderIcon,
			".gdshaderinc" => _gdshaderincIcon,
			".csproj" => CsprojIcon,
			_ => _genericFileIcon
		};
		var overlayTexture = fileExtension switch
		{
			".props" => _propsFileOverlayIcon,
			".config" or ".resx" => _configFileOverlayIcon,
			".targets" => _targetsFileOverlayIcon,
			_ => null
		};
		if (overlayTexture is null && texture == _genericFileIcon)
		{
			overlayTexture = _unknownFileOverlayIcon;
		}

		return (texture, overlayTexture);
	}
}
