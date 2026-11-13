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
	/// What the labels show - the sources and their latest versions, and the installed info - so an update that changes neither is skipped
	private (InstalledNugetPackageInfo? InstalledInfo, string Sources)? _appliedValues;
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
		ApplyValues();
		_button.Pressed += () => _ = Task.GodotRun(() => PackageSelected.Invoke(PackageResult));
	}

	/// Shows the package's latest results, e.g. as more sources answer - reusing this entry rather than replacing it. Call on the UI thread
	public void SetPackageResult(IdePackageResult packageResult)
	{
		PackageResult = packageResult;
		if (IsNodeReady()) ApplyValues();
	}

	private void ApplyValues()
	{
		if (PackageResult is null) return;
		var values = (PackageResult.InstalledNugetPackageInfo, string.Join('|', PackageResult.PackageFromSources.Select(s => $"{s.Source.Source}:{s.PackageSearchMetadata.Identity.Version}")));
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
		var highestVersionPackageFromSource = PackageResult.PackageFromSources
			.MaxBy(p => p.PackageSearchMetadata.Identity.Version);
		if (installedPackagedInfo?.ProjectPackageReferences.TrueForAll(s => s.InstalledVersion != highestVersionPackageFromSource.PackageSearchMetadata.Identity.Version) is true)
		{
			_latestVersionLabel.Text = highestVersionPackageFromSource.PackageSearchMetadata.Identity.Version.ToNormalizedString();
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