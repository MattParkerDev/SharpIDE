using Godot;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Nuget;

public partial class NugetPackageDetails : VBoxContainer
{
	private TextureRect _packageIconTextureRect = null!;
	private Label _packageNameLabel = null!;
	private OptionButton _versionOptionButton = null!;
	private OptionButton _nugetSourceOptionButton = null!;
	private VBoxContainer _projectsVBoxContainer = null!;

	private IdePackageResult? _package;
	
	private readonly Texture2D _defaultIconTextureRect = ResourceLoader.Load<Texture2D>("uid://b5ih61vdjv5e6");
	private readonly Texture2D _warningIconTextureRect = ResourceLoader.Load<Texture2D>("uid://pd3h5qfjn8pb");
	private readonly PackedScene _packageDetailsProjectEntryScene = ResourceLoader.Load<PackedScene>("uid://5uan5u23cr2s");
	
	[Inject] private readonly NugetPackageIconCacheService _nugetPackageIconCacheService = null!;
	[Inject] private readonly NugetClientService _nugetClientService = null!;
	public override void _Ready()
	{
		_packageIconTextureRect = GetNode<TextureRect>("%PackageIconTextureRect");
		_packageNameLabel = GetNode<Label>("%PackageNameLabel");
		_versionOptionButton = GetNode<OptionButton>("%VersionOptionButton");
		_nugetSourceOptionButton = GetNode<OptionButton>("%NugetSourceOptionButton");
		_projectsVBoxContainer = GetNode<VBoxContainer>("%ProjectsVBoxContainer");
		_nugetSourceOptionButton.ItemSelected += OnNugetSourceSelected;
		
		_projectsVBoxContainer.QueueFreeChildren();
	}

	public async Task SetPackage(IdePackageResult package)
	{
		_package = package;
		var iconTask = _nugetPackageIconCacheService.GetNugetPackageIcon(_package.PackageId, _package.PackageFromSources.First().PackageSearchMetadata.IconUrl);
		await this.InvokeAsync(() =>
		{
			_packageNameLabel.Text = package.PackageId;
			Visible = true;
		});
		var (iconBytes, iconFormat) = await iconTask;
		var imageTexture = ImageTextureHelper.GetImageTextureFromBytes(iconBytes, iconFormat) ?? _defaultIconTextureRect;
		await this.InvokeAsync(() =>
		{
			_packageIconTextureRect.Texture = imageTexture;
			
			_versionOptionButton.Clear();
			_nugetSourceOptionButton.Clear();
			foreach (var packageFromSource in package.PackageFromSources)
			{
				_nugetSourceOptionButton.AddItem(packageFromSource.Source.Name);
			}
			_nugetSourceOptionButton.Selected = 0;
			OnNugetSourceSelected(0);
		});
	}

	public async Task SetProjects(HashSet<SharpIdeProjectModel> projects)
	{
		var scenes = projects.Select(s =>
		{
			var scene = _packageDetailsProjectEntryScene.Instantiate<PackageDetailsProjectEntry>();
			scene.ProjectModel = s;
			return scene;
		}).ToList();
		await this.InvokeAsync(() =>
		{
			_projectsVBoxContainer.QueueFreeChildren();
			foreach (var scene in scenes)
			{
				_projectsVBoxContainer.AddChild(scene);
			}
		});
	}
	
	private async void OnNugetSourceSelected(long sourceIndex)
	{
		var source = _package!.PackageFromSources[(int)sourceIndex];
		var results = await _nugetClientService.GetAllVersionsOfPackageInSource(source.PackageSearchMetadata.Identity.Id, source.Source);
		await this.InvokeAsync(() =>
		{
			_versionOptionButton.Clear();
			foreach (var (index, metadata) in results.Index())
			{
				_versionOptionButton.AddItem(metadata.Identity.Version.ToNormalizedString());
				//_versionOptionButton.SetItemIcon(index, _warningIconTextureRect);
			}
			_versionOptionButton.Selected = 0;
		});
	}
}