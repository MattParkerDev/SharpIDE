using System.Diagnostics;
using Godot;
using NuGet.Versioning;
using SharpIDE.Application;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Godot.Features.ActivityListener;

namespace SharpIDE.Godot.Features.Nuget;

public partial class NugetPanel : Control
{
	private VBoxContainer _packagesToUpdateVboxContainer = null!;
	private VBoxContainer _installedPackagesVboxContainer = null!;
	private VBoxContainer _implicitlyInstalledPackagesItemList = null!;
	private VBoxContainer _availablePackagesItemList = null!;
	private OptionButton _solutionOrProjectOptionButton = null!;
	private Button _refreshButton = null!;
	private TabContainer _tabContainer = null!;
	private Control _discoverTab = null!;
	/// Whether the Discover tab's search results have been loaded (or are loading) - they're loaded when the tab is first opened, and again on refresh while it's open
	private bool _searchResultsRequested;
	/// Cancels the search results still arriving when a refresh starts another load
	private CancellationTokenSource? _searchResultsCts;
	/// Cancels the installed packages' results still arriving when another solution or project is selected, or a refresh starts another load
	private CancellationTokenSource? _installedPackagesCts;
	
	private Label _packagesToUpdateSlnOrProjectNameLabel = null!;
	private Label _packagesToUpdateResultCountLabel = null!;
	private Label _installedPackagesSlnOrProjectNameLabel = null!;
	private Label _installedPackagesResultCountLabel = null!;
	private Label _implicitlyInstalledPackagesSlnOrProjectNameLabel = null!;
	private Label _implicitlyInstalledPackagesResultCountLabel = null!;
	
	private ProgressBar _packagesToUpdateProgressBar = null!;
	private ProgressBar _installedPackagesProgressBar = null!;
	private ProgressBar _implicitlyInstalledPackagesProgressBar = null!;
	private ProgressBar _packageSearchProgressBar = null!;
	
	private NugetPackageDetails _nugetPackageDetails = null!;

	private CheckBox _packagesToUpdateSelectAllCheckBox = null!;
	private Button _packagesToUpdateUpdateButton = null!;
	/// The rows of the Packages to Update section, each with its checkbox
	private readonly List<(CheckBox CheckBox, IdePackageResult Package)> _updateRows = [];
	// Each list's entries (for the updates section, its rows), by package id - kept so new results update them, rather than replacing them
	private readonly Dictionary<string, Control> _updateRowNodes = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Control> _installedEntryNodes = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Control> _implicitlyInstalledEntryNodes = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Control> _availableEntryNodes = new(StringComparer.OrdinalIgnoreCase);
	/// Ticked packages, by id - kept across refreshes, for those still with an update
	private readonly HashSet<string> _selectedUpdatePackageIds = new(StringComparer.OrdinalIgnoreCase);
	private bool _updatingPackages;

	private SharpIdeSolutionModel? _solution;
	
	[Inject] private readonly NugetClientService _nugetClientService = null!;
	[Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;
	[Inject] private readonly ActivityMonitor _activityMonitor = null!;
	[Inject] private readonly PackageReferenceService _packageReferenceService = null!;
	
	private readonly PackedScene _packageEntryScene = ResourceLoader.Load<PackedScene>("uid://cqc2xlt81ju8s");
	private readonly Texture2D _csprojIcon = ResourceLoader.Load<Texture2D>("uid://cqt30ma6xgder");

	// we use this to access the project for the dropdown
	private List<SharpIdeProjectModel?> _projects = null!;
	private IdePackageResult? _selectedPackageResult = null!;

	public override void _Ready()
	{
		_packagesToUpdateVboxContainer = GetNode<VBoxContainer>("%PackagesToUpdateVBoxContainer");
		_packagesToUpdateSlnOrProjectNameLabel = GetNode<Label>("%PackagesToUpdateSlnOrProjectNameLabel");
		_packagesToUpdateResultCountLabel = GetNode<Label>("%PackagesToUpdateResultCountLabel");
		_packagesToUpdateProgressBar = GetNode<ProgressBar>("%PackagesToUpdateProgressBar");
		_packagesToUpdateProgressBar.Visible = false;
		_packagesToUpdateVboxContainer.QueueFreeChildren();
		_packagesToUpdateSelectAllCheckBox = GetNode<CheckBox>("%PackagesToUpdateSelectAllCheckBox");
		_packagesToUpdateUpdateButton = GetNode<Button>("%PackagesToUpdateUpdateButton");
		_packagesToUpdateSelectAllCheckBox.Toggled += OnSelectAllUpdatesToggled;
		_packagesToUpdateUpdateButton.Pressed += UpdateSelectedPackages;
		_installedPackagesVboxContainer =GetNode<VBoxContainer>("%InstalledPackagesVBoxContainer");
		_implicitlyInstalledPackagesItemList = GetNode<VBoxContainer>("%ImplicitlyInstalledPackagesVBoxContainer");
		_availablePackagesItemList = GetNode<VBoxContainer>("%AvailablePackagesVBoxContainer");
		_solutionOrProjectOptionButton = GetNode<OptionButton>("%SolutionOrProjectOptionButton");
		_refreshButton = GetNode<Button>("%RefreshButton");
		_nugetPackageDetails = GetNode<NugetPackageDetails>("%NugetPackageDetails");
		_installedPackagesSlnOrProjectNameLabel = GetNode<Label>("%InstalledPackagesSlnOrProjectNameLabel");
		_installedPackagesResultCountLabel = GetNode<Label>("%InstalledPackagesResultCountLabel");
		_implicitlyInstalledPackagesSlnOrProjectNameLabel = GetNode<Label>("%ImplicitlyInstalledPackagesSlnOrProjectNameLabel");
		_implicitlyInstalledPackagesResultCountLabel = GetNode<Label>("%ImplicitlyInstalledPackagesResultCountLabel");
		_installedPackagesProgressBar = GetNode<ProgressBar>("%InstalledPackagesProgressBar");
		_implicitlyInstalledPackagesProgressBar = GetNode<ProgressBar>("%ImplicitlyInstalledPackagesProgressBar");
		_packageSearchProgressBar = GetNode<ProgressBar>("%PackageSearchProgressBar");
		_installedPackagesProgressBar.Visible = false;
		_implicitlyInstalledPackagesProgressBar.Visible = false;
		_packageSearchProgressBar.Visible = false;
		_nugetPackageDetails.Visible = false;
		_tabContainer = GetNode<TabContainer>("%NugetTabContainer");
		_discoverTab = _tabContainer.GetNode<Control>("Discover");
		_tabContainer.TabChanged += _ => LoadSearchResultsIfDiscoverOpen();
		_refreshButton.Pressed += () =>
		{
			OnSolutionOrProjectSelected(_solutionOrProjectOptionButton.Selected);
			_searchResultsRequested = false;
			LoadSearchResultsIfDiscoverOpen();
		};
		_nugetPackageDetails.PackageReferencesChanged += OnPackageReferencesChanged;
		_installedPackagesVboxContainer.QueueFreeChildren();
		_implicitlyInstalledPackagesItemList.QueueFreeChildren();
		_availablePackagesItemList.QueueFreeChildren();
		_activityMonitor.ActivityChanged.Subscribe(OnActivityChanged);
		
		_ = Task.GodotRun(_AsyncReady);
	}

	private async Task _AsyncReady()
	{
		await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
		_solution = _sharpIdeSolutionAccessor.SolutionModel;
		_projects = [null!, .._solution!.AllProjects.OrderBy(s => s.Name.Value)]; // So that index 0 is solution // Probably should use Item Metadata instead of this
		await this.InvokeAsync(() =>
		{
			foreach (var project in _projects.Skip(1))
			{
				_solutionOrProjectOptionButton.AddIconItem(_csprojIcon, project!.Name.Value);
			}
			_solutionOrProjectOptionButton.ItemSelected += OnSolutionOrProjectSelected;
		});
	}

	public override void _ExitTree()
	{
		_activityMonitor.ActivityChanged.Unsubscribe(OnActivityChanged);
	}

	private void OnSolutionOrProjectSelected(long index)
	{
		var slnOrProject = (ISolutionOrProject?)_projects[(int)index] ?? _solution!;
		// Cancel runs the cancelled load's callbacks on this (the UI) thread - CancelAsync runs them off it
		_ = _installedPackagesCts?.CancelAsync();
		_installedPackagesCts = new CancellationTokenSource();
		var cancellationToken = _installedPackagesCts.Token;
		_ = Task.GodotRun(async () =>
		{
			if (_solution is null) throw new InvalidOperationException("Solution is null but should not be");
			_ = Task.GodotRun(() => SetSolutionOrProjectNameLabels(slnOrProject));
			_ = Task.GodotRun(() => PopulateInstalledPackages(slnOrProject, cancellationToken));
		});
	}

	/// The search results don't depend on the selected solution or project, so they're only loaded once the Discover tab is opened
	private void LoadSearchResultsIfDiscoverOpen()
	{
		if (_searchResultsRequested || _tabContainer.GetCurrentTabControl() != _discoverTab) return;
		_searchResultsRequested = true;
		_ = _searchResultsCts?.CancelAsync();
		_searchResultsCts = new CancellationTokenSource();
		var cancellationToken = _searchResultsCts.Token;
		_ = Task.GodotRun(() => PopulateSearchResults(cancellationToken));
	}

	/// A project's reference was changed from the details pane - its project has been re-evaluated, so read the installed packages again
	private async Task OnPackageReferencesChanged()
	{
		await this.InvokeAsync(() => OnSolutionOrProjectSelected(_solutionOrProjectOptionButton.Selected));
	}

	private async Task OnPackageSelected(IdePackageResult packageResult)
	{
		_selectedPackageResult = packageResult;
		await _nugetPackageDetails.SetPackage(packageResult);
	}

	private async Task SetDetailsProjects(ISolutionOrProject slnOrProject)
	{
		var projects = slnOrProject switch
		{
			SharpIdeSolutionModel => _projects.Skip(1).ToHashSet(),
			SharpIdeProjectModel projectModel => [projectModel],
			_ => throw new InvalidOperationException("Unknown solution or project type")
		};
		await _nugetPackageDetails.SetProjects(projects!);
	}

	private async Task SetSolutionOrProjectNameLabels(ISolutionOrProject slnOrProject)
	{
		var text = slnOrProject switch
		{
			SharpIdeSolutionModel => "Solution",
			SharpIdeProjectModel projectModel => projectModel.Name.Value,
			_ => throw new InvalidOperationException("Unknown solution or project type")
		};
		await this.InvokeAsync(() =>
		{
			_packagesToUpdateSlnOrProjectNameLabel.Text = text;
			_installedPackagesSlnOrProjectNameLabel.Text = text;
			_implicitlyInstalledPackagesSlnOrProjectNameLabel.Text = text;
		});
	}

	private async Task PopulateSearchResults(CancellationToken cancellationToken)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(NugetPanel)}.{nameof(PopulateSearchResults)}");
		// The tab can be opened before the solution has loaded
		await _sharpIdeSolutionAccessor.SolutionReadyTcs.Task;
		try
		{
			// Each update is the whole list so far - the fastest source's results first, then the other sources each has them on, then the slower sources'
			await foreach (var result in _nugetClientService.GetTop100Results(_sharpIdeSolutionAccessor.SolutionModel!.DirectoryPath, cancellationToken))
			{
				await this.InvokeAsync(() =>
				{
					if (cancellationToken.IsCancellationRequested) return;
					SyncPackageEntries(_availablePackagesItemList, _availableEntryNodes, result);
				});
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A refresh started another load
		}
	}

	private async Task PopulateInstalledPackages(ISolutionOrProject slnOrProject, CancellationToken cancellationToken)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(NugetPanel)}.{nameof(PopulateInstalledPackages)}");
		var setDetailsProjectsTask = SetDetailsProjects(slnOrProject);
		var msbuildEvalTask = slnOrProject switch
		{
			SharpIdeSolutionModel solutionModel => (Task)Task.WhenAll(solutionModel.AllProjects.Select(s => s.MsBuildEvaluationProjectTask)),
			SharpIdeProjectModel projectModel => (Task)projectModel.MsBuildEvaluationProjectTask,
			_ => throw new InvalidOperationException("Unknown solution or project type")
		};
		await msbuildEvalTask;
		var projects = slnOrProject switch
		{
			SharpIdeSolutionModel solutionModel => solutionModel.AllProjects.ToList(),
			SharpIdeProjectModel projectModel => [projectModel],
			_ => throw new InvalidOperationException("Unknown solution or project type")
		};
		await setDetailsProjectsTask;
		try
		{
			var (installedPackages, unrestoredProjects) = await ProjectEvaluation.GetPackageReferencesForProjects(projects, cancellationToken: cancellationToken);
			var isFirst = true;
			// Each update is the whole list - first straight away with what's installed, then with each source's latest versions as they arrive
			await foreach (var idePackageResult in _nugetClientService.GetPackagesForInstalledPackages(slnOrProject.DirectoryPath, installedPackages, cancellationToken))
			{
				await ShowInstalledPackages(idePackageResult, unrestoredProjects, isFirst, cancellationToken);
				isFirst = false;
			}
			await this.InvokeAsync(() =>
			{
				if (cancellationToken.IsCancellationRequested) return;
				// Every source has answered - forget ticked packages no longer listed, e.g. those just updated
				_selectedUpdatePackageIds.IntersectWith(_updateRows.Select(r => r.Package.PackageId));
				UpdateUpdateControls();
			});
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A refresh, or another solution or project being selected, started another load
		}
	}

	private async Task ShowInstalledPackages(List<IdePackageResult> idePackageResult, List<SharpIdeProjectModel> unrestoredProjects, bool isFirst, CancellationToken cancellationToken)
	{
		var transitivePackages = idePackageResult.Where(s => s.InstalledNugetPackageInfo!.IsPrimarilyTransitive).ToList();
		var directPackages = idePackageResult.Where(s => s.InstalledNugetPackageInfo!.IsPrimarilyTransitive is false).ToList();
		var updatePackages = directPackages.Where(HasUpdate).ToList();
		if (_selectedPackageResult is { } selected && cancellationToken.IsCancellationRequested is false)
		{
			var updatedPackageResult = idePackageResult.SingleOrDefault(p => p.PackageId.Equals(selected.PackageId, StringComparison.OrdinalIgnoreCase));
			// The details are reloaded with the newly read installed info, then only when another source turns out to have the package - not for every update
			if (isFirst || (updatedPackageResult is not null && updatedPackageResult.PackageFromSources.Count != selected.PackageFromSources.Count))
			{
				// No longer installed anywhere (it was just removed) - still show it, as installed nowhere
				await OnPackageSelected(updatedPackageResult ?? selected with { InstalledNugetPackageInfo = null });
			}
		}
		await this.InvokeAsync(() =>
		{
			if (cancellationToken.IsCancellationRequested) return;
			// A node can only have one parent, so the updates section has its own entries
			SetUpdateRows(updatePackages);
			SyncPackageEntries(_implicitlyInstalledPackagesItemList, _implicitlyInstalledEntryNodes, transitivePackages);
			SyncPackageEntries(_installedPackagesVboxContainer, _installedEntryNodes, directPackages);
			// Projects that aren't loaded or restored have no project.assets.json to read their packages from
			_installedPackagesResultCountLabel.Text = unrestoredProjects.Count is 0
				? directPackages.Count.ToString()
				: $"{directPackages.Count} ({unrestoredProjects.Count} {(unrestoredProjects.Count is 1 ? "project" : "projects")} not restored)";
			_installedPackagesResultCountLabel.TooltipText = unrestoredProjects.Count is 0
				? string.Empty
				: $"Not restored, so their packages aren't listed:\n{string.Join("\n", unrestoredProjects.Select(p => p.Name.Value))}";
			_implicitlyInstalledPackagesResultCountLabel.Text = transitivePackages.Count.ToString();
			_packagesToUpdateResultCountLabel.Text = updatePackages.Count.ToString();
		});
	}

	/// Whether a source has a newer version than one a project references itself - implicit (transitive) references are updated through what brings them in,
	/// and those the SDK adds through the SDK
	private static bool HasUpdate(IdePackageResult packageResult) => GetUpdates(packageResult).Count > 0;

	/// Each project reference to move to the latest version any source has
	private static List<(SharpIdeProjectModel Project, NuGetVersion Version)> GetUpdates(IdePackageResult packageResult)
	{
		if (packageResult.InstalledNugetPackageInfo is not { } installedInfo || packageResult.PackageFromSources.Count is 0) return [];
		var latestVersion = packageResult.PackageFromSources.Max(s => s.PackageSearchMetadata.Identity.Version)!;
		return installedInfo.ProjectPackageReferences
			.Where(r => r is { IsTopLevel: true, IsAutoReferenced: false } && r.InstalledVersion < latestVersion)
			.Select(r => (r.Project, latestVersion))
			.DistinctBy(u => u.Project)
			.ToList();
	}

	private PackageEntry CreateEntry(IdePackageResult packageResult)
	{
		var entry = _packageEntryScene.Instantiate<PackageEntry>();
		entry.PackageResult = packageResult;
		entry.PackageSelected += OnPackageSelected;
		return entry;
	}

	/// Makes the container show these packages, in this order - updating the entries already there rather than replacing them, as results arrive several
	/// times a second and rebuilding every entry (and reloading its icon) each time made the UI lag. Call on the UI thread
	private static void SyncEntries(Container container, Dictionary<string, Control> nodes, List<IdePackageResult> packages, Func<IdePackageResult, Control> create, Action<Control, IdePackageResult> update)
	{
		var packageIds = packages.Select(p => p.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var (packageId, node) in nodes.Where(n => packageIds.Contains(n.Key) is false).ToList())
		{
			nodes.Remove(packageId);
			// Removed now, not at the end of the frame, so the indices below are right
			container.RemoveChild(node);
			node.QueueFree();
		}
		foreach (var (index, package) in packages.Index())
		{
			if (nodes.TryGetValue(package.PackageId, out var node))
			{
				update(node, package);
			}
			else
			{
				node = create(package);
				nodes[package.PackageId] = node;
				container.AddChild(node);
			}
			if (node.GetIndex() != index) container.MoveChild(node, index);
		}
	}

	private void SyncPackageEntries(Container container, Dictionary<string, Control> nodes, List<IdePackageResult> packages) =>
		SyncEntries(container, nodes, packages, CreateEntry, (node, package) => ((PackageEntry)node).SetPackageResult(package));

	/// Shows each package in a row with a checkbox, ticked if it was before. Call on the UI thread
	private void SetUpdateRows(List<IdePackageResult> packages)
	{
		SyncEntries(_packagesToUpdateVboxContainer, _updateRowNodes, packages, package =>
		{
			var packageId = package.PackageId;
			var checkBox = new CheckBox { ButtonPressed = _selectedUpdatePackageIds.Contains(packageId), SizeFlagsVertical = SizeFlags.ShrinkCenter };
			checkBox.Toggled += pressed =>
			{
				if (pressed) _selectedUpdatePackageIds.Add(packageId);
				else _selectedUpdatePackageIds.Remove(packageId);
				UpdateUpdateControls();
			};
			var entry = CreateEntry(package);
			entry.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			var row = new HBoxContainer();
			row.AddThemeConstantOverride(ThemeStringNames.Separation, 0);
			row.AddChild(checkBox);
			row.AddChild(entry);
			return row;
		}, (row, package) => row.GetChild<PackageEntry>(1).SetPackageResult(package));
		_updateRows.Clear();
		_updateRows.AddRange(packages.Select(p => (_updateRowNodes[p.PackageId].GetChild<CheckBox>(0), p)));
		// Ticked packages not (yet) listed are only forgotten once every source has answered - a slow source may yet have their update
		UpdateUpdateControls();
	}

	private void OnSelectAllUpdatesToggled(bool pressed)
	{
		foreach (var (checkBox, package) in _updateRows)
		{
			checkBox.SetPressedNoSignal(pressed);
			if (pressed) _selectedUpdatePackageIds.Add(package.PackageId);
			else _selectedUpdatePackageIds.Remove(package.PackageId);
		}
		UpdateUpdateControls();
	}

	/// The select-all checkbox ticked when every row is, and the Update button enabled when any is. Call on the UI thread
	private void UpdateUpdateControls()
	{
		var selectedCount = _updateRows.Count(r => r.CheckBox.ButtonPressed);
		_packagesToUpdateSelectAllCheckBox.SetPressedNoSignal(_updateRows.Count > 0 && selectedCount == _updateRows.Count);
		_packagesToUpdateSelectAllCheckBox.Disabled = _updatingPackages || _updateRows.Count is 0;
		foreach (var (checkBox, _) in _updateRows) checkBox.Disabled = _updatingPackages;
		_packagesToUpdateUpdateButton.Disabled = _updatingPackages || selectedCount is 0;
		if (_updatingPackages is false) _packagesToUpdateUpdateButton.Text = selectedCount is 0 ? "Update" : $"Update ({selectedCount})";
	}

	/// Moves each ticked package to its latest version in every project referencing an older one - one project at a time, as each change restores - then reads the packages again
	private void UpdateSelectedPackages()
	{
		if (_updatingPackages) return;
		var changes = _updateRows
			.Where(r => r.CheckBox.ButtonPressed)
			.SelectMany(r => GetUpdates(r.Package).Select(u => (r.Package.PackageId, u.Project, u.Version)))
			.ToList();
		if (changes.Count is 0) return;
		_updatingPackages = true;
		UpdateUpdateControls();
		_ = Task.GodotRun(async () =>
		{
			var failures = new List<string>();
			var anySucceeded = false;
			try
			{
				foreach (var (index, (packageId, project, version)) in changes.Index())
				{
					await this.InvokeAsync(() => _packagesToUpdateUpdateButton.Text = $"Updating {index + 1}/{changes.Count}...");
					var result = await _packageReferenceService.AddOrUpdateAsync(project, packageId, version);
					anySucceeded |= result.Success;
					if (result.Success is false) failures.Add($"{packageId} {version.ToNormalizedString()} in {project.Name.Value}: {result.Error ?? "the change failed"}");
				}
			}
			finally
			{
				await this.InvokeAsync(() =>
				{
					_updatingPackages = false;
					UpdateUpdateControls();
					if (failures.Count > 0) ShowUpdateFailures(failures);
				});
			}
			if (anySucceeded) await OnPackageReferencesChanged();
		});
	}

	private void ShowUpdateFailures(List<string> failures)
	{
		var dialog = new AcceptDialog
		{
			Title = failures.Count is 1 ? "An update failed" : $"{failures.Count} updates failed",
			DialogText = string.Join('\n', failures),
			DialogAutowrap = true,
			Exclusive = true,
			Transient = true
		};
		dialog.VisibilityChanged += () =>
		{
			if (dialog.Visible is false) dialog.QueueFree();
		};
		AddChild(dialog);
		dialog.PopupCentered(new Vector2I(700, 0));
	}
	
	private async Task OnActivityChanged(Activity activity)
	{
		var isOccurring = !activity.IsStopped;
		if (activity.DisplayName == $"{nameof(NugetPanel)}.{nameof(PopulateInstalledPackages)}")
		{
			await this.InvokeAsync(() =>
			{
				_packagesToUpdateProgressBar.Visible = isOccurring;
				_installedPackagesProgressBar.Visible = isOccurring;
				_implicitlyInstalledPackagesProgressBar.Visible = isOccurring;
			});
		}
		else if (activity.DisplayName == $"{nameof(NugetPanel)}.{nameof(PopulateSearchResults)}")
		{
			await this.InvokeAsync(() => _packageSearchProgressBar.Visible = isOccurring);
		}
	}
}