using System.Diagnostics;
using Godot;
using SharpIDE.Application;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;
using SharpIDE.Godot.Features.ActivityListener;

namespace SharpIDE.Godot.Features.Nuget;

public partial class NugetPanel : Control
{
	private VBoxContainer _installedPackagesVboxContainer = null!;
	private VBoxContainer _implicitlyInstalledPackagesItemList = null!;
	private VBoxContainer _availablePackagesItemList = null!;
	private OptionButton _solutionOrProjectOptionButton = null!;
	
	private Label _installedPackagesSlnOrProjectNameLabel = null!;
	private Label _installedPackagesResultCountLabel = null!;
	private Label _implicitlyInstalledPackagesSlnOrProjectNameLabel = null!;
	private Label _implicitlyInstalledPackagesResultCountLabel = null!;
	
	private ProgressBar _installedPackagesProgressBar = null!;
	
	private NugetPackageDetails _nugetPackageDetails = null!;

	private SharpIdeSolutionModel? _solution;
	
	[Inject] private readonly NugetClientService _nugetClientService = null!;
	[Inject] private readonly SharpIdeSolutionAccessor _sharpIdeSolutionAccessor = null!;
	[Inject] private readonly ActivityMonitor _activityMonitor = null!;
	
	private readonly PackedScene _packageEntryScene = ResourceLoader.Load<PackedScene>("uid://cqc2xlt81ju8s");
	private readonly Texture2D _csprojIcon = ResourceLoader.Load<Texture2D>("uid://cqt30ma6xgder");

	// we use this to access the project for the dropdown
	private List<SharpIdeProjectModel?> _projects = null!;
	private IdePackageResult? _selectedPackageResult = null!;

	public override void _Ready()
	{
		_installedPackagesVboxContainer = GetNode<VBoxContainer>("%InstalledPackagesVBoxContainer");
		_implicitlyInstalledPackagesItemList = GetNode<VBoxContainer>("%ImplicitlyInstalledPackagesVBoxContainer");
		_availablePackagesItemList = GetNode<VBoxContainer>("%AvailablePackagesVBoxContainer");
		_solutionOrProjectOptionButton = GetNode<OptionButton>("%SolutionOrProjectOptionButton");
		_nugetPackageDetails = GetNode<NugetPackageDetails>("%NugetPackageDetails");
		_installedPackagesSlnOrProjectNameLabel = GetNode<Label>("%InstalledPackagesSlnOrProjectNameLabel");
		_installedPackagesResultCountLabel = GetNode<Label>("%InstalledPackagesResultCountLabel");
		_implicitlyInstalledPackagesSlnOrProjectNameLabel = GetNode<Label>("%ImplicitlyInstalledPackagesSlnOrProjectNameLabel");
		_implicitlyInstalledPackagesResultCountLabel = GetNode<Label>("%ImplicitlyInstalledPackagesResultCountLabel");
		_installedPackagesProgressBar = GetNode<ProgressBar>("%InstalledPackagesProgressBar");
		_installedPackagesProgressBar.Visible = false;
		_nugetPackageDetails.Visible = false;
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
		_projects = [null!, .._solution!.AllProjects.OrderBy(s => s.Name)]; // So that index 0 is solution // Probably should use Item Metadata instead of this
		await this.InvokeAsync(() =>
		{
			foreach (var project in _projects.Skip(1))
			{
				_solutionOrProjectOptionButton.AddIconItem(_csprojIcon, project!.Name);
			}
			_solutionOrProjectOptionButton.ItemSelected += OnSolutionOrProjectSelected;
		});
		OnSolutionOrProjectSelected(0);
	}

	public override void _ExitTree()
	{
		_activityMonitor.ActivityChanged.Unsubscribe(OnActivityChanged);
	}

	private void OnSolutionOrProjectSelected(long index)
	{
		var slnOrProject = (ISolutionOrProject?)_projects[(int)index] ?? _solution!;
		_ = Task.GodotRun(async () =>
		{
			if (_solution is null) throw new InvalidOperationException("Solution is null but should not be");
			_ = Task.GodotRun(() => SetSolutionOrProjectNameLabels(slnOrProject));
			_ = Task.GodotRun(() => PopulateInstalledPackages(slnOrProject));
			_ = Task.GodotRun(PopulateSearchResults);
		});
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
			SharpIdeProjectModel projectModel => projectModel.Name,
			_ => throw new InvalidOperationException("Unknown solution or project type")
		};
		await this.InvokeAsync(() =>
		{
			_installedPackagesSlnOrProjectNameLabel.Text = text;
			_implicitlyInstalledPackagesSlnOrProjectNameLabel.Text = text;
		});
	}

	private async Task PopulateSearchResults()
	{
		return;
		var result = await _nugetClientService.GetTop100Results(_solution!.DirectoryPath);
		var scenes = result.Select(s =>
		{
			var scene = _packageEntryScene.Instantiate<PackageEntry>();
			scene.PackageResult = s;
			scene.PackageSelected += OnPackageSelected;
			return scene;
		}).ToList();
		await this.InvokeAsync(() =>
		{
			_availablePackagesItemList.QueueFreeChildren();
			foreach (var scene in scenes)
			{
				_availablePackagesItemList.AddChild(scene);
			}
		});
	}

	private async Task PopulateInstalledPackages(ISolutionOrProject slnOrProject)
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
		var installedPackages = await ProjectEvaluation.GetPackageReferencesForProjects(projects);
		var idePackageResult = await _nugetClientService.GetPackagesForInstalledPackages(slnOrProject.DirectoryPath, installedPackages);
		var scenes = idePackageResult.Select(s =>
		{
			var scene = _packageEntryScene.Instantiate<PackageEntry>();
			scene.PackageResult = s;
			scene.PackageSelected += OnPackageSelected;
			return scene;
		}).ToList();
		var transitiveScenes = scenes.Where(s => s.PackageResult.InstalledNugetPackageInfo!.IsPrimarilyTransitive).ToList();
		var directScenes = scenes.Except(transitiveScenes).ToList();
		await setDetailsProjectsTask;
		if (_selectedPackageResult is not null)
		{
			var updatedPackageResult = idePackageResult.SingleOrDefault(p => p.PackageId.Equals(_selectedPackageResult.PackageId, StringComparison.OrdinalIgnoreCase));
			if (updatedPackageResult is not null)
			{
				_selectedPackageResult = updatedPackageResult;
				await OnPackageSelected(_selectedPackageResult);
			}
		}
		await this.InvokeAsync(() =>
		{
			_installedPackagesVboxContainer.QueueFreeChildren();
			_implicitlyInstalledPackagesItemList.QueueFreeChildren();
			foreach (var transitiveScene in transitiveScenes)
			{
				_implicitlyInstalledPackagesItemList.AddChild(transitiveScene);
			}
			foreach (var directScene in directScenes)
			{
				_installedPackagesVboxContainer.AddChild(directScene);
			}
			_installedPackagesResultCountLabel.Text = directScenes.Count.ToString();
			_implicitlyInstalledPackagesResultCountLabel.Text = transitiveScenes.Count.ToString();
		});
	}
	
	private async Task OnActivityChanged(Activity activity)
	{
		var isOccurring = !activity.IsStopped;
		if (activity.DisplayName == $"{nameof(NugetPanel)}.{nameof(PopulateInstalledPackages)}")
		{
			await this.InvokeAsync(() => _installedPackagesProgressBar.Visible = isOccurring);
		}
	}
}