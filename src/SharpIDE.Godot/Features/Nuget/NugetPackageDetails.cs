using Godot;
using NuGet.Versioning;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.Nuget;

public partial class NugetPackageDetails : VBoxContainer
{
	private TextureRect _packageIconTextureRect = null!;
	private Label _packageNameLabel = null!;
	private FoldableContainer _infoFoldableContainer = null!;
	private OptionButton _versionOptionButton = null!;
	private OptionButton _nugetSourceOptionButton = null!;
	private VBoxContainer _projectsVBoxContainer = null!;

	private IdePackageResult? _package;
	private NuGetVersion? _selectedVersion;

	/// Raised off the UI thread once a project's reference to the package has been changed
	public event Func<Task>? PackageReferencesChanged;
	
	private readonly Texture2D _defaultIconTextureRect = ResourceLoader.Load<Texture2D>("uid://b5ih61vdjv5e6");
	private readonly Texture2D _warningIconTextureRect = ResourceLoader.Load<Texture2D>("uid://pd3h5qfjn8pb");
	private readonly PackedScene _packageDetailsProjectEntryScene = ResourceLoader.Load<PackedScene>("uid://5uan5u23cr2s");
	
	[Inject] private readonly NugetPackageIconCacheService _nugetPackageIconCacheService = null!;
	[Inject] private readonly NugetClientService _nugetClientService = null!;
	public override void _Ready()
	{
		_packageIconTextureRect = GetNode<TextureRect>("%PackageIconTextureRect");
		_packageNameLabel = GetNode<Label>("%PackageNameLabel");
		_infoFoldableContainer = GetNode<FoldableContainer>("%InfoFoldableContainer");
		_versionOptionButton = GetNode<OptionButton>("%VersionOptionButton");
		_nugetSourceOptionButton = GetNode<OptionButton>("%NugetSourceOptionButton");
		_projectsVBoxContainer = GetNode<VBoxContainer>("%ProjectsVBoxContainer");
		_nugetSourceOptionButton.ItemSelected += index => _ = Task.GodotRun(() => OnNugetSourceSelected(index));
		_versionOptionButton.ItemSelected += VersionSelected;
		
		_projectsVBoxContainer.QueueFreeChildren();
	}

	private void VersionSelected(long index)
	{
		var selectedVersion = _versionOptionButton.GetItemMetadata((int)index).As<RefCountedContainer<NuGetVersion>>();
		_selectedVersion = selectedVersion?.Item;
		foreach (var child in _projectsVBoxContainer.GetChildren().OfType<PackageDetailsProjectEntry>())
		{
			child.VersionSelectedInDetails = selectedVersion?.Item;
			child.DisplayRelevantButtons();
		}
	}

	private Task OnPackageReferencesChanged() => PackageReferencesChanged?.Invoke() ?? Task.CompletedTask;

	public async Task SetPackage(IdePackageResult package)
	{
		if (_package is null || _package.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase) is false) _selectedVersion = null;
		_package = package;
		var iconTask = _nugetPackageIconCacheService.GetNugetPackageIcon(_package.PackageId, _package.PackageFromSources.FirstOrDefault().PackageSearchMetadata?.IconUrl);
		await this.InvokeAsync(() =>
		{
			_packageNameLabel.Text = package.PackageId;
			Visible = true;
		});
		var (iconBytes, iconFormat) = await iconTask;
		var imageTexture = ImageTextureHelper.GetImageTextureFromBytes(iconBytes, iconFormat) ?? _defaultIconTextureRect;
		await SetProjectPackageReferences(package.InstalledNugetPackageInfo?.ProjectPackageReferences ?? []);
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
		});
		// No source returned the package (unreachable, or it isn't on any enabled source) - there are no versions to list
		if (package.PackageFromSources.Count is 0) return;
		await OnNugetSourceSelected(0);
	}
	
	public async Task SetProjects(HashSet<SharpIdeProjectModel> projects)
	{
		var scenes = projects.Select(s =>
		{
			var scene = _packageDetailsProjectEntryScene.Instantiate<PackageDetailsProjectEntry>();
			scene.ProjectModel = s;
			scene.PackageReferencesChanged += OnPackageReferencesChanged;
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

	public async Task SetProjectPackageReferences(List<ProjectPackageReference> projectPackageReferences)
	{
		await this.InvokeAsync(() =>
		{
			var scenes = _projectsVBoxContainer.GetChildren().OfType<PackageDetailsProjectEntry>().ToList();
			foreach (var scene in scenes)
			{
				scene.PackageId = _package?.PackageId;
				// Each project's reference to this package - none for those that don't reference it, rather than the previous package's
				var projectPackageReference = projectPackageReferences.SingleOrDefault(r => r.Project == scene.ProjectModel);
				if (projectPackageReference is null)
				{
					scene.ClearInstallInfo();
					continue;
				}
				scene.ProjectPackageReference = projectPackageReference;
				scene.SetValues();
			}
		});
	}
	
	private async Task OnNugetSourceSelected(long sourceIndex)
	{
		var packageFromSource = _package!.PackageFromSources[(int)sourceIndex];
		var results = await _nugetClientService.GetAllVersionsOfPackageInSource(packageFromSource.PackageSearchMetadata.Identity.Id, packageFromSource.Source);
		await this.InvokeAsync(() =>
		{
			_infoFoldableContainer.Title = packageFromSource.PackageSearchMetadata.Description;
			_versionOptionButton.Clear();
			foreach (var (index, metadata) in results.Index())
			{
				_versionOptionButton.AddItem(metadata.Identity.Version.ToNormalizedString(), index);
				_versionOptionButton.SetItemMetadata(index, new RefCountedContainer<NuGetVersion>(metadata.Identity.Version));
				//_versionOptionButton.SetItemIcon(index, _warningIconTextureRect);
			}
			// Keep the version chosen before a refresh, e.g. after a downgrade
			var selectedIndex = _selectedVersion is null ? -1 : results.FindIndex(m => m.Identity.Version == _selectedVersion);
			if (selectedIndex < 0) selectedIndex = 0;
			_versionOptionButton.Selected = selectedIndex;
			VersionSelected(selectedIndex);
		});
	}
}