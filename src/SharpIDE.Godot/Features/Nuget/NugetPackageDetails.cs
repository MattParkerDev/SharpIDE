using Godot;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using SharpIDE.Application;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.Common;
using SharpIDE.Godot.Features.Problems;

namespace SharpIDE.Godot.Features.Nuget;

public partial class NugetPackageDetails : VBoxContainer
{
	private TextureRect _packageIconTextureRect = null!;
	private Label _packageNameLabel = null!;
	private CustomFoldableContainer _infoFoldableContainer = null!;
	private RichTextLabel _infoRichTextLabel = null!;
	/// The selected source's metadata for each version, as the version dropdown lists them
	private List<IPackageSearchMetadata> _versionMetadata = [];
	private List<(NuGetVersion Version, string Path)> _localInstallations = [];
	private OptionButton _versionOptionButton = null!;
	private OptionButton _nugetSourceOptionButton = null!;
	private VBoxContainer _projectsVBoxContainer = null!;
	private Button _installAllButton = null!;
	private Button _upgradeAllButton = null!;
	private Button _downgradeAllButton = null!;
	private Button _removeAllButton = null!;
	/// A change across several projects is under way - they run one at a time, as each is a restore
	private bool _changingProjects;

	private IdePackageResult? _package;
	private NuGetVersion? _selectedVersion;

	/// Raised off the UI thread once a project's reference to the package has been changed
	public event Func<Task>? PackageReferencesChanged;
	
	private readonly Texture2D _defaultIconTextureRect = ResourceLoader.Load<Texture2D>("uid://b5ih61vdjv5e6");
	private readonly Texture2D _warningIconTextureRect = ResourceLoader.Load<Texture2D>("uid://pd3h5qfjn8pb");
	private readonly PackedScene _packageDetailsProjectEntryScene = ResourceLoader.Load<PackedScene>("uid://5uan5u23cr2s");
	
	[Inject] private readonly NugetPackageIconCacheService _nugetPackageIconCacheService = null!;
	[Inject] private readonly NugetClientService _nugetClientService = null!;
	[Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;
	public override void _Ready()
	{
		_packageIconTextureRect = GetNode<TextureRect>("%PackageIconTextureRect");
		_packageNameLabel = GetNode<Label>("%PackageNameLabel");
		_infoFoldableContainer = GetNode<CustomFoldableContainer>("%InfoFoldableContainer");
		_infoRichTextLabel = GetNode<RichTextLabel>("%InfoRichTextLabel");
		SetUpInfo();
		_versionOptionButton = GetNode<OptionButton>("%VersionOptionButton");
		_nugetSourceOptionButton = GetNode<OptionButton>("%NugetSourceOptionButton");
		_projectsVBoxContainer = GetNode<VBoxContainer>("%ProjectsVBoxContainer");
		_nugetSourceOptionButton.ItemSelected += index => _ = Task.GodotRun(() => OnNugetSourceSelected(index));
		_versionOptionButton.ItemSelected += VersionSelected;
		_installAllButton = GetNode<Button>("%InstallAllButton");
		_installAllButton.Pressed += () => ShowChangeDialog(PackageChangeKind.Install);
		_upgradeAllButton = GetNode<Button>("%UpgradeAllButton");
		_downgradeAllButton = GetNode<Button>("%DowngradeAllButton");
		_removeAllButton = GetNode<Button>("%RemoveAllButton");
		_upgradeAllButton.Pressed += () => ShowChangeDialog(PackageChangeKind.Upgrade);
		_downgradeAllButton.Pressed += () => ShowChangeDialog(PackageChangeKind.Downgrade);
		_removeAllButton.Pressed += () => ShowChangeDialog(PackageChangeKind.Remove);

		_projectsVBoxContainer.QueueFreeChildren();
		DisplayRelevantButtons();
	}

	/// Not those SetProjects has replaced, which are freed at the end of the frame
	private List<PackageDetailsProjectEntry> ProjectEntries => _projectsVBoxContainer.GetChildren().OfType<PackageDetailsProjectEntry>().Where(e => e.IsQueuedForDeletion() is false).ToList();

	/// Each top-level button shows when at least one project can take its change. Call on the UI thread
	private void DisplayRelevantButtons()
	{
		var entries = ProjectEntries;
		// Whenever any project doesn't reference the package itself
		_installAllButton.Visible = _changingProjects is false && entries.Any(e => e.CanChange(PackageChangeKind.Install));
		_upgradeAllButton.Visible = _changingProjects is false && entries.Any(e => e.CanChange(PackageChangeKind.Upgrade));
		_downgradeAllButton.Visible = _changingProjects is false && entries.Any(e => e.CanChange(PackageChangeKind.Downgrade));
		_removeAllButton.Visible = _changingProjects is false && entries.Any(e => e.CanChange(PackageChangeKind.Remove));
	}

	private void ShowChangeDialog(PackageChangeKind kind)
	{
		if (_package is null || _changingProjects) return;
		var packageId = _package.PackageId;
		var newVersion = kind is PackageChangeKind.Remove ? string.Empty : _selectedVersion?.ToNormalizedString() ?? string.Empty;
		var rows = ProjectEntries
			.Where(e => e.CanChange(kind))
			.OrderBy(e => e.ProjectModel.Name.Value, StringComparer.OrdinalIgnoreCase)
			.Select(e => new PackageChangeDialogRow(e, OldVersionText(e.ProjectPackageReference), newVersion))
			.ToList();
		if (rows.Count is 0) return;

		var oldVersions = rows.Select(r => r.OldVersion).Distinct().ToList();
		var fromVersion = oldVersions.Count is 1 ? $" {oldVersions[0]}" : string.Empty;
		var (title, okText) = kind switch
		{
			PackageChangeKind.Install => ($"Install {packageId} {newVersion} in projects", "Install"),
			PackageChangeKind.Upgrade => ($"Upgrade {packageId}{fromVersion} to {newVersion} in projects", "Upgrade"),
			PackageChangeKind.Downgrade => ($"Downgrade {packageId}{fromVersion} to {newVersion} in projects", "Downgrade"),
			_ => ($"Remove {packageId}{fromVersion} from projects", "Remove")
		};
		var dialog = new PackageChangeDialog();
		dialog.Setup(title, okText, packageId, rows);
		dialog.RowsConfirmed += confirmedRows => ChangeProjects(confirmedRows, remove: kind is PackageChangeKind.Remove);
		AddChild(dialog);
		dialog.PopupForRows();
	}

	/// Blank where the project has no reference, in brackets where a package brings it in, as the project rows show it
	private static string OldVersionText(ProjectPackageReference? reference) => reference switch
	{
		null => string.Empty,
		{ IsTransitive: true } => $"({reference.InstalledVersion.ToNormalizedString()})",
		_ => reference.InstalledVersion.ToNormalizedString()
	};

	/// Changes each project in turn - a change restores, and only one can run at a time - then raises PackageReferencesChanged once. Call on the UI thread
	private void ChangeProjects(List<PackageChangeDialogRow> rows, bool remove)
	{
		var changes = rows
			.Select((row, index) => (row.Entry, Change: row.Entry.BeginChange(remove, queued: index > 0)))
			.Where(c => c.Change is not null)
			.ToList();
		if (changes.Count is 0) return;
		_changingProjects = true;
		DisplayRelevantButtons();
		_ = Task.GodotRun(async () =>
		{
			var anySucceeded = false;
			try
			{
				foreach (var (entry, change) in changes)
				{
					var result = await entry.RunChangeAsync(change!);
					anySucceeded |= result.Success;
				}
			}
			finally
			{
				await this.InvokeAsync(() =>
				{
					_changingProjects = false;
					DisplayRelevantButtons();
				});
			}
			if (anySucceeded) await OnPackageReferencesChanged();
		});
	}

	private void VersionSelected(long index)
	{
		var selectedVersion = _versionOptionButton.GetItemMetadata((int)index).As<RefCountedContainer<NuGetVersion>>();
		_selectedVersion = selectedVersion?.Item;
		ShowInfo(_versionMetadata.Find(m => m.Identity.Version == _selectedVersion));
		foreach (var child in _projectsVBoxContainer.GetChildren().OfType<PackageDetailsProjectEntry>())
		{
			child.VersionSelectedInDetails = selectedVersion?.Item;
			child.DisplayRelevantButtons();
		}
		DisplayRelevantButtons();
	}

	/// Links open in the browser, or for a local installation, the file manager
	private void SetUpInfo()
	{
		_infoRichTextLabel.MetaClicked += meta => OS.ShellOpen(meta.AsString());
		// Overriding a child's theme items only notifies the child, so this doesn't loop
		ThemeChanged += ApplyInfoTheme;
		ApplyInfoTheme();
		ShowInfo(null);
	}

	/// The info text in the pane's label colour (a RichTextLabel's is otherwise white), with [b] in a semibold weight of the theme's font, as the Info title
	private void ApplyInfoTheme()
	{
		_infoRichTextLabel.AddThemeColorOverride(DefaultColorName, GetThemeColor(ThemeStringNames.FontColor, Gray700LabelType));
		_infoRichTextLabel.AddThemeFontOverride(BoldFontName, new FontVariation
		{
			BaseFont = GetThemeDefaultFont(),
			VariationOpentype = new global::Godot.Collections.Dictionary { [TextServerManager.GetPrimaryInterface().NameToTag("weight")] = 600 }
		});
	}

	private static readonly StringName BoldFontName = "bold_font";
	private static readonly StringName DefaultColorName = "default_color";
	private static readonly StringName Gray700LabelType = "Gray700Label";
	private const string LinkColor = "589df6";

	/// Shows the version's details - its description, links, author and publish date - and where the package is restored locally. Call on the UI thread
	private void ShowInfo(IPackageSearchMetadata? metadata)
	{
		var description = metadata?.Description?.Trim() ?? string.Empty;
		_infoFoldableContainer.Summary = description;
		var text = new System.Text.StringBuilder();
		if (description.Length > 0) text.AppendLine(Escape(description));

		if (metadata is not null)
		{
			var links = new List<string>();
			if (metadata.PackageDetailsUrl is { } detailsUrl) links.Add(Link(detailsUrl.Host.EndsWith("nuget.org", StringComparison.OrdinalIgnoreCase) ? "NuGet.org" : detailsUrl.Host, detailsUrl.AbsoluteUri));
			if (metadata.ProjectUrl is { } projectUrl) links.Add(Link(projectUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "GitHub" : "Project website", projectUrl.AbsoluteUri));
			var license = metadata.LicenseMetadata?.License;
			if (metadata.LicenseUrl is { } licenseUrl) links.Add(Link(string.IsNullOrWhiteSpace(license) ? "License" : license, licenseUrl.AbsoluteUri));
			else if (string.IsNullOrWhiteSpace(license) is false) links.Add(Escape(license));
			links.Add(Link("Google", $"https://www.google.com/search?q={Uri.EscapeDataString(metadata.Identity.Id)}"));
			text.AppendLine($"[b]Links:[/b] {string.Join("  ", links)}");
			if (string.IsNullOrWhiteSpace(metadata.Authors) is false) text.AppendLine($"[b]Author:[/b] {Escape(metadata.Authors)}");
			// nuget.org gives unlisted packages a 1900 publish date
			if (metadata.Published is { Year: > 1900 } published) text.AppendLine($"[b]Published:[/b] {published.LocalDateTime:MMMM d, yyyy}");
		}

		if (_localInstallations.Count > 0)
		{
			text.AppendLine("[b]Local Installations:[/b]");
			foreach (var (_, path) in _localInstallations) text.AppendLine(Link(path, path, external: false));
		}

		_infoRichTextLabel.Text = text.ToString().TrimEnd();
		_infoFoldableContainer.Visible =_infoRichTextLabel.Text.Length > 0;
	}

	private static string Link(string text, string target, bool external = true) =>
		$"[url=\"{target.Replace("\"", "%22")}\"][color={LinkColor}]{Escape(text)}{(external ? " ↗" : string.Empty)}[/color][/url]";

	/// So BBCode in the text shows as written
	private static string Escape(string text) => text.Replace("[", "[lb]");

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
		if (package.PackageFromSources.Count is 0)
		{
			await this.InvokeAsync(() =>
			{
				_versionMetadata = [];
				_localInstallations = [];
				ShowInfo(null);
			});
			return;
		}
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
			DisplayRelevantButtons();
		});
	}
	
	private async Task OnNugetSourceSelected(long sourceIndex)
	{
		var packageFromSource = _package!.PackageFromSources[(int)sourceIndex];
		var packageId = packageFromSource.PackageSearchMetadata.Identity.Id;
		var results = await _nugetClientService.GetAllVersionsOfPackageInSource(packageId, packageFromSource.Source);
		var localInstallations = _sharpIdeSolutionAccessor.SolutionModel is { } solution
			? _nugetClientService.GetLocalInstallations(packageId, solution.DirectoryPath)
			: [];
		await this.InvokeAsync(() =>
		{
			_versionMetadata = results;
			_localInstallations = localInstallations;
			// Until a version is selected, e.g. when the source has none
			ShowInfo(packageFromSource.PackageSearchMetadata);
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