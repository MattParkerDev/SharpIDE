using System.Collections.Immutable;
using Godot;
using SharpIDE.Application.Features.Nuget;

namespace SharpIDE.Godot.Features.Nuget;

public partial class PackageEntry : MarginContainer
{
    private Label _packageNameLabel = null!;
    private Label _currentVersionLabel = null!;
    private Label _latestVersionLabel = null!;
    private HBoxContainer _sourceNamesContainer = null!;
    private TextureRect _packageIconTextureRect = null!;
    
    private static readonly Color Source_1_Color = new Color("629655");
    private static readonly Color Source_2_Color = new Color("008989");
    private static readonly Color Source_3_Color = new Color("8d75a8");
    private static readonly Color Source_4_Color = new Color("966a00");
    private static readonly Color Source_5_Color = new Color("efaeae");
    
    private static readonly ImmutableArray<Color> SourceColors =
    [
        Source_1_Color,
        Source_2_Color,
        Source_3_Color,
        Source_4_Color,
        Source_5_Color
    ];
    
    [Inject] private readonly NugetPackageIconCacheService _nugetPackageIconCacheService = null!;
    
    public IdePackageResult PackageResult { get; set; } = null!;
    public override void _Ready()
    {
        _packageNameLabel = GetNode<Label>("%PackageNameLabel");
        _currentVersionLabel = GetNode<Label>("%CurrentVersionLabel");
        _latestVersionLabel = GetNode<Label>("%LatestVersionLabel");
        _sourceNamesContainer = GetNode<HBoxContainer>("%SourceNamesHBoxContainer");
        _packageIconTextureRect = GetNode<TextureRect>("%PackageIconTextureRect");
        ApplyValues();
    }
    
    private void ApplyValues()
    {
        if (PackageResult is null) return;
        _packageNameLabel.Text = PackageResult.PackageId;
        _currentVersionLabel.Text = string.Empty;
        var highestVersionPackageFromSource = PackageResult.PackageFromSources
            .MaxBy(p => p.PackageSearchMetadata.Identity.Version);
        _latestVersionLabel.Text = highestVersionPackageFromSource.PackageSearchMetadata.Identity.Version.ToNormalizedString();
        _sourceNamesContainer.QueueFreeChildren();

        _ = Task.GodotRun(async () =>
        {
            var (iconBytes, iconFormat) = await _nugetPackageIconCacheService.GetNugetPackageIcon(PackageResult.PackageId, PackageResult.PackageFromSources.First().PackageSearchMetadata.IconUrl);
            var image = new Image();
            var error = iconFormat switch
            {
                NugetPackageIconFormat.Png => image.LoadPngFromBuffer(iconBytes),
                NugetPackageIconFormat.Jpg => image.LoadJpgFromBuffer(iconBytes),
                _ => Error.FileUnrecognized
            };
            if (error is Error.Ok)
            {
                image.Resize(32, 32, Image.Interpolation.Lanczos); // Probably should cache resized images instead
                var loadedImageTexture = ImageTexture.CreateFromImage(image);
                await this.InvokeAsync(() => _packageIconTextureRect.Texture = loadedImageTexture);
            }
        });
        
        foreach (var (index, packageFromSource) in PackageResult.PackageFromSources.Index())
        {
            var label = new Label { Text = packageFromSource.Source.Name };
            var labelColour = SourceColors[index % SourceColors.Length];
            label.AddThemeColorOverride(ThemeStringNames.FontColor, labelColour);
            _sourceNamesContainer.AddChild(label);
        }
    }
}