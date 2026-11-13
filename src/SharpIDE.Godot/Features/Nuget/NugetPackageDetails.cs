using Godot;
using Microsoft.CodeAnalysis.Threading;
using NuGet.Frameworks;
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
	private CustomFoldableContainer _dependenciesFoldableContainer = null!;
	private RichTextLabel _dependenciesRichTextLabel = null!;
	/// The version whose details the Info and Dependencies sections show
	private IPackageSearchMetadata? _shownMetadata;
	/// The selected source's metadata for each version, as the version dropdown lists them
	/// The selected source's versions, prereleases included, newest first - those listed are filtered from these
	private List<IPackageSearchMetadata> _versionMetadata = [];
	/// Whether prerelease versions are listed. Only touched on the UI thread
	private bool _includePrerelease;
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
	/// Cancels a source's versions still loading when another source, or package, is selected
	private readonly CancellationSeries _sourceVersionsCancellationSeries = new();

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
		_dependenciesFoldableContainer = GetNode<CustomFoldableContainer>("%DependenciesFoldableContainer");
		_dependenciesRichTextLabel = GetNode<RichTextLabel>("%DependenciesRichTextLabel");
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
		// -1 when the source has no versions to choose from
		var selectedVersion = index >= 0 && index < _versionOptionButton.ItemCount
			? _versionOptionButton.GetItemMetadata((int)index).As<RefCountedContainer<NuGetVersion>>()
			: null;
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
		// The version ranges' grey is the theme's, so they're written again when it changes
		ThemeChanged += () => ShowDependencies(_shownMetadata);
		ShowInfo(null);
	}

	private static readonly StringName Gray600LabelType = "Gray600Label";
	private const string LinkColor = "589df6";

	/// Shows the version's details - its description, links, author and publish date, where the package is restored locally, and its dependencies for each framework.
	/// Call on the UI thread
	private void ShowInfo(IPackageSearchMetadata? metadata)
	{
		_shownMetadata = metadata;
		ShowDependencies(metadata);
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

	/// Each framework the version targets, as listed in its nuspec, with the packages it depends on there and the versions it accepts
	private void ShowDependencies(IPackageSearchMetadata? metadata)
	{
		var groups = metadata?.DependencySets?.ToList() ?? [];
		_dependenciesFoldableContainer.Visible = groups.Count > 0;
		_dependenciesFoldableContainer.Summary = string.Join(", ", groups.Select(g => FormatFramework(g.TargetFramework)));
		var rangeColor = GetThemeColor(ThemeStringNames.FontColor, Gray600LabelType);
		var label = _dependenciesRichTextLabel;
		label.Clear();
		foreach (var (index, group) in groups.Index())
		{
			if (index > 0) label.Newline();
			label.AddText(FormatFramework(group.TargetFramework));
			if (group.Packages.Any() is false) continue;
			// An indent starts its own line, so only the dependencies after the first need one
			label.PushIndent(1);
			foreach (var (dependencyIndex, dependency) in group.Packages.Index())
			{
				var range = dependency.VersionRange.ToString("p", VersionRangeFormatter.Instance).Replace(">=", "≥").Replace("<=", "≤");
				if (dependencyIndex > 0) label.Newline();
				label.AddText($"{dependency.Id} ");
				label.PushColor(rangeColor);
				label.AddText($"({range})");
				label.Pop();
			}
			label.Pop();
		}
	}

	/// As NuGet.org shows it - .NET 8.0 rather than net8.0, .NETStandard 2.0, .NETFramework 4.7.2
	private static string FormatFramework(NuGetFramework framework)
	{
		if (framework.IsAny || framework.IsAgnostic) return "Any";
		// net5.0 and later are .NETCoreApp underneath
		var name = framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp && framework.Version.Major >= 5 ? ".NET" : framework.Framework;
		var platform = framework.HasPlatform
			? $" ({framework.Platform}{(framework.PlatformVersion > FrameworkConstants.EmptyVersion ? $" {FormatVersion(framework.PlatformVersion)}" : string.Empty)})"
			: string.Empty;
		return $"{name} {FormatVersion(framework.Version)}{platform}";
	}

	/// At least major.minor, without trailing zero parts - 4.7.2, 10.0.19041, 35.0
	private static string FormatVersion(Version version) => (version.Revision, version.Build) switch
	{
		( > 0, _) => version.ToString(4),
		(_, > 0) => version.ToString(3),
		_ => version.ToString(2)
	};

	private static string Link(string text, string target, bool external = true) =>
		$"[url=\"{target.Replace("\"", "%22")}\"][color={LinkColor}]{Escape(text)}{(external ? " ↗" : string.Empty)}[/color][/url]";

	/// So BBCode in the text shows as written
	private static string Escape(string text) => text.Replace("[", "[lb]");

	private Task OnPackageReferencesChanged() => PackageReferencesChanged?.Invoke() ?? Task.CompletedTask;

	public async Task SetPackage(IdePackageResult package)
	{
		if (_package is null || _package.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase) is false) _selectedVersion = null;
		_package = package;
		var firstSource = _package.PackageFromSources.FirstOrDefault();
		var iconTask = _nugetPackageIconCacheService.GetNugetPackageIcon(_package.PackageId, firstSource.PackageSearchMetadata?.IconUrl, firstSource.Source);
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
			// The previous package's versions may still be loading
			_sourceVersionsCancellationSeries.CreateNext();
			await this.InvokeAsync(() =>
			{
				_versionMetadata = [];
				_localInstallations = [];
				// No version to select - so the projects' buttons don't offer the previous package's
				VersionSelected(-1);
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
		// Another source (or package) selected while this one's versions load replaces them - they'd otherwise be shown if they arrived last
		var cancellationToken = _sourceVersionsCancellationSeries.CreateNext();
		var packageFromSource = _package!.PackageFromSources[(int)sourceIndex];
		var packageId = packageFromSource.PackageSearchMetadata.Identity.Id;
		List<IPackageSearchMetadata> results;
		try
		{
			results = await _nugetClientService.GetAllVersionsOfPackageInSource(packageId, packageFromSource.Source, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// The source failed (e.g. it needs credentials) - show it as having no versions, rather than leaving the previous source's
			results = [];
		}
		var localInstallations = _sharpIdeSolutionAccessor.SolutionModel is { } solution
			? _nugetClientService.GetLocalInstallations(packageId, solution.DirectoryPath)
			: [];
		await this.InvokeAsync(() =>
		{
			if (cancellationToken.IsCancellationRequested) return;
			_versionMetadata = results;
			_localInstallations = localInstallations;
			// Until a version is selected, e.g. when the source has none
			ShowInfo(packageFromSource.PackageSearchMetadata);
			ShowVersions();
		});
	}

	/// Prereleases are filtered from the source's versions in memory, so toggling them doesn't ask the source again. Call on the UI thread
	public void SetIncludePrerelease(bool includePrerelease)
	{
		if (_includePrerelease == includePrerelease) return;
		_includePrerelease = includePrerelease;
		if (_package is not null) ShowVersions();
	}

	/// Lists the source's versions - without prereleases unless they're included, or the source only has prereleases - keeping the version chosen if it's
	/// still listed (e.g. after a downgrade, or toggling prereleases). Call on the UI thread
	private void ShowVersions()
	{
		var versions = _includePrerelease || _versionMetadata.TrueForAll(m => m.Identity.Version.IsPrerelease)
			? _versionMetadata
			: _versionMetadata.FindAll(m => m.Identity.Version.IsPrerelease is false);
		_versionOptionButton.Clear();
		foreach (var (index, metadata) in versions.Index())
		{
			_versionOptionButton.AddItem(metadata.Identity.Version.ToNormalizedString(), index);
			_versionOptionButton.SetItemMetadata(index, new RefCountedContainer<NuGetVersion>(metadata.Identity.Version));
			//_versionOptionButton.SetItemIcon(index, _warningIconTextureRect);
		}
		var selectedIndex = _selectedVersion is null ? -1 : versions.FindIndex(m => m.Identity.Version == _selectedVersion);
		if (selectedIndex < 0) selectedIndex = versions.Count > 0 ? 0 : -1;
		_versionOptionButton.Selected = selectedIndex;
		VersionSelected(selectedIndex);
	}
}