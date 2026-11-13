using System.Collections.Immutable;
using Godot;
using NuGet.Versioning;
using SharpIDE.Application.Features.Nuget;

namespace SharpIDE.Godot.Features.Nuget;

public partial class PackageEntry : MarginContainer
{
	private Button _button = null!;
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
	
	/// The Prerelease toggle's red, in NugetPanel.tscn
	private static readonly Color PrereleaseColor = new Color(0.9490196f, 0.5137255f, 0.4f);
	/// The latest version's colour when it isn't a prerelease, as the scene sets it
	private Color _latestVersionColor;

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
	/// Whether the latest version shown may be a prerelease. Set before the entry is added to the tree, then through SetPackageResult
	public bool IncludePrerelease { get; set; }
	/// What the labels show - the sources and their latest versions, the installed info, and whether prereleases are included - so an update that changes none is skipped
	private (InstalledNugetPackageInfo? InstalledInfo, string Sources, bool IncludePrerelease)? _appliedValues;
	/// The icon is loaded once, from the first source to have the package
	private bool _iconRequested;

	public override void _Ready()
	{
		_button = GetNode<Button>("Button");
		_packageNameLabel = GetNode<Label>("%PackageNameLabel");
		_installedVersionLabel = GetNode<Label>("%InstalledVersionLabel");
		_latestVersionLabel = GetNode<Label>("%LatestVersionLabel");
		_sourceNamesContainer = GetNode<HBoxContainer>("%SourceNamesHBoxContainer");
		_packageIconTextureRect = GetNode<TextureRect>("%PackageIconTextureRect");
		_latestVersionLabel.Text = string.Empty;
		_latestVersionColor = _latestVersionLabel.GetThemeColor(ThemeStringNames.FontColor);
		ApplyValues();
		_button.Pressed += () => _ = Task.GodotRun(() => PackageSelected.Invoke(PackageResult));
	}

	/// Shows the package's latest results, e.g. as more sources answer - reusing this entry rather than replacing it. Call on the UI thread
	public void SetPackageResult(IdePackageResult packageResult, bool includePrerelease)
	{
		PackageResult = packageResult;
		IncludePrerelease = includePrerelease;
		if (IsNodeReady()) ApplyValues();
	}

	private void ApplyValues()
	{
		if (PackageResult is null) return;
		var values = (PackageResult.InstalledNugetPackageInfo, string.Join('|', PackageResult.PackageFromSources.Select(s => $"{s.Source.Source}:{s.PackageSearchMetadata.Identity.Version}:{s.LatestPrereleaseVersion}")), IncludePrerelease);
		if (_appliedValues == values) return;
		_appliedValues = values;
		_packageNameLabel.Text = PackageResult.PackageId;
		_latestVersionLabel.Text = string.Empty;
		var installedPackagedInfo = PackageResult.InstalledNugetPackageInfo;
		if (installedPackagedInfo?.IsPrimarilyTransitive is true && installedPackagedInfo.ProjectPackageReferences.Any(p => p.DependentPackages?.Count is not 0) is true)
		{
			var transitiveOriginsGroupedByVersion = installedPackagedInfo.ProjectPackageReferences.SelectMany(s => s.DependentPackages ?? [])
				.GroupBy(t => t.RequestedVersion)
				.Select(g => new
				{
					RequestedVersion = g.Key,
					PackageNames = g.Select(t => t.PackageName).Distinct().ToList()
				})
				.ToList();
			_button.TooltipText = $"""
								  Implicitly Referenced Versions
								  {transitiveOriginsGroupedByVersion.AsValueEnumerable().Select(t => $"{t.RequestedVersion.ToString("p", VersionRangeFormatter.Instance)} by {string.Join(", ", t.PackageNames)}").JoinToString('\n')}
								  """;
		}
		_installedVersionLabel.Text = GetInstalledVersionsText(installedPackagedInfo);
		_sourceNamesContainer.QueueFreeChildren();
		// No source returned the package (unreachable, or it isn't on any enabled source)
		if (PackageResult.PackageFromSources.Count is 0) return;
		var latestVersion = PackageResult.PackageFromSources.Max(p => p.GetLatestVersion(IncludePrerelease))!;
		if (installedPackagedInfo?.ProjectPackageReferences.TrueForAll(s => s.InstalledVersion != latestVersion) is true)
		{
			_latestVersionLabel.Text = latestVersion.ToNormalizedString();
			_latestVersionLabel.AddThemeColorOverride(ThemeStringNames.FontColor, latestVersion.IsPrerelease ? PrereleaseColor : _latestVersionColor);
		}

		if (_iconRequested is false)
		{
			_iconRequested = true;
			var packageId = PackageResult.PackageId;
			var firstSource = PackageResult.PackageFromSources.First();
			_ = Task.GodotRun(async () =>
			{
				var (iconBytes, iconFormat) = await _nugetPackageIconCacheService.GetNugetPackageIcon(packageId, firstSource.PackageSearchMetadata.IconUrl, firstSource.Source);
				var imageTexture = ImageTextureHelper.GetImageTextureFromBytes(iconBytes, iconFormat);
				if (imageTexture is not null)
				{
					await this.InvokeAsync(() =>
					{
						// The entry may have been removed (e.g. another solution or project selected) and freed while the icon loaded
						if (IsInstanceValid(_packageIconTextureRect)) _packageIconTextureRect.Texture = imageTexture;
					});
				}
			});
		}
		
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