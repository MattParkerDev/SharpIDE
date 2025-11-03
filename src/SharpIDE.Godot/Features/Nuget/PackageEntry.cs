using System.Collections.Immutable;
using Godot;
using NuGet.Versioning;
using SharpIDE.Application.Features.Nuget;

namespace SharpIDE.Godot.Features.Nuget;

public partial class PackageEntry : MarginContainer
{
	private Button _button;
	private Label _packageNameLabel = null!;
	private Label _installedVersionLabel = null!;
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

	public event Func<IdePackageResult, Task> PackageSelected = null!;
	
	[Inject] private readonly NugetPackageIconCacheService _nugetPackageIconCacheService = null!;
	
	public IdePackageResult PackageResult { get; set; } = null!;
	public override void _Ready()
	{
		_button = GetNode<Button>("Button");
		_packageNameLabel = GetNode<Label>("%PackageNameLabel");
		_installedVersionLabel = GetNode<Label>("%InstalledVersionLabel");
		_latestVersionLabel = GetNode<Label>("%LatestVersionLabel");
		_sourceNamesContainer = GetNode<HBoxContainer>("%SourceNamesHBoxContainer");
		_packageIconTextureRect = GetNode<TextureRect>("%PackageIconTextureRect");
		_latestVersionLabel.Text = string.Empty;
		ApplyValues();
		_button.Pressed += async () => await PackageSelected.Invoke(PackageResult);
	}
	
	private void ApplyValues()
	{
		if (PackageResult is null) return;
		_packageNameLabel.Text = PackageResult.PackageId;
		var installedPackagedInfo = PackageResult.InstalledNugetPackageInfo;
		if (installedPackagedInfo?.IsPrimarilyTransitive is true && installedPackagedInfo.ProjectPackageReferences.Any(p => p.DependentPackages?.Count is not 0) is true)
		{
			var transitiveOriginsGroupedByVersion = installedPackagedInfo.ProjectPackageReferences.SelectMany(s => s.DependentPackages ?? [])
				.GroupBy(t => t.RequestedVersion)
				.Select(g => new
				{
					RequestedVersion = g.Key,
					PackageNames = g.Select(t => t.PackageName).ToList()
				})
				.ToList();
			_button.TooltipText = $"""
								  Implicitly Referenced Versions
								  {string.Join("\n", transitiveOriginsGroupedByVersion.Select(t => $"{t.RequestedVersion.ToString("p", VersionRangeFormatter.Instance)} by {string.Join(", ", t.PackageNames)}"))}
								  """;
		}
		_installedVersionLabel.Text = GetInstalledVersionsText(installedPackagedInfo);
		var highestVersionPackageFromSource = PackageResult.PackageFromSources
			.MaxBy(p => p.PackageSearchMetadata.Identity.Version);
		if (installedPackagedInfo?.ProjectPackageReferences.TrueForAll(s => s.InstalledVersion != highestVersionPackageFromSource.PackageSearchMetadata.Identity.Version) is true)
		{
			_latestVersionLabel.Text = highestVersionPackageFromSource.PackageSearchMetadata.Identity.Version.ToNormalizedString();
		}
		_sourceNamesContainer.QueueFreeChildren();

		_ = Task.GodotRun(async () =>
		{
			var (iconBytes, iconFormat) = await _nugetPackageIconCacheService.GetNugetPackageIcon(PackageResult.PackageId, PackageResult.PackageFromSources.First().PackageSearchMetadata.IconUrl);
			var imageTexture = ImageTextureHelper.GetImageTextureFromBytes(iconBytes, iconFormat);
			if (imageTexture is not null)
			{
				await this.InvokeAsync(() => _packageIconTextureRect.Texture = imageTexture);
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
	
	private string GetInstalledVersionsText(InstalledNugetPackageInfo? packageInfo)
	{
		if (packageInfo is null) return string.Empty;
		
		var versions = packageInfo.ProjectPackageReferences
			.Select(p => p.InstalledVersion.ToNormalizedString())
			.Distinct()
			.ToList();
		var text = packageInfo.IsPrimarilyTransitive ? $"({string.Join(", ", versions)})" : string.Join(", ", versions);
		return text;
	}
}