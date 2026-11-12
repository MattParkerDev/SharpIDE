using Godot;
using NuGet.Versioning;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.Nuget;

public partial class PackageDetailsProjectEntry : MarginContainer
{
    private static readonly Color ErrorColor = new("fc776a");

    private Label _projectNameLabel = null!;
    private Label _installedVersionLabel = null!;
    private Button _addButton = null!;
    private Button _upgradeButton = null!;
    private Button _downgradeButton = null!;
    private Button _removeButton = null!;
    private bool _changing;

    [Inject] private readonly PackageReferenceService _packageReferenceService = null!;

    public SharpIdeProjectModel ProjectModel { get; set; } = null!;
    public string? PackageId { get; set; }
    public ProjectPackageReference? ProjectPackageReference { get; set; }
    public NuGetVersion? VersionSelectedInDetails { get; set; }

    /// Raised off the UI thread once a change of this project's reference to the package is written, restored and re-evaluated
    public event Func<Task>? PackageReferencesChanged;

    public override void _Ready()
    {
        _projectNameLabel = GetNode<Label>("%ProjectNameLabel");;
        _installedVersionLabel = GetNode<Label>("%InstalledVersionLabel");
        _addButton = GetNode<Button>("%AddButton");
        _upgradeButton = GetNode<Button>("%UpgradeButton");
        _downgradeButton = GetNode<Button>("%DowngradeButton");
        _removeButton = GetNode<Button>("%RemoveButton");
        _installedVersionLabel.Text = string.Empty;
        _addButton.Pressed += () => RequestChange(remove: false);
        _upgradeButton.Pressed += () => RequestChange(remove: false);
        _downgradeButton.Pressed += () => RequestChange(remove: false);
        _removeButton.Pressed += () => RequestChange(remove: true);
        SetValues();
    }

    public void SetValues()
    {
        if (ProjectModel == null) return;
        _projectNameLabel.Text = ProjectModel.Name.Value;
        if (ProjectPackageReference == null) return;
        _installedVersionLabel.RemoveThemeColorOverride(ThemeStringNames.FontColor);
        var isTransitive = ProjectPackageReference.IsTransitive;
        var installedVersion = ProjectPackageReference.InstalledVersion;
        _installedVersionLabel.Text = isTransitive ? $"({installedVersion?.ToNormalizedString()})" : installedVersion?.ToNormalizedString();
        _installedVersionLabel.TooltipText = string.Empty;

        if (isTransitive)
        {
            var transitiveOriginsGroupedByVersion = ProjectPackageReference.DependentPackages!.GroupBy(t => t.RequestedVersion)
                .Select(g => new
                {
                    RequestedVersion = g.Key,
                    PackageNames = g.Select(t => t.PackageName).Distinct().ToList()
                })
                .ToList();
            _installedVersionLabel.TooltipText = $"""
                                                  Implicitly Referenced Versions
                                                  {transitiveOriginsGroupedByVersion.AsValueEnumerable().Select(t => $"{t.RequestedVersion.ToString("p", VersionRangeFormatter.Instance)} by {string.Join(", ", t.PackageNames)}").JoinToString('\n')}
                                                  """;
        }
        DisplayRelevantButtons();
    }

    public void ClearInstallInfo()
    {
        _installedVersionLabel.Text = string.Empty;
        _installedVersionLabel.TooltipText = string.Empty;
        ProjectPackageReference = null;
        DisplayRelevantButtons();
    }

    public void DisplayRelevantButtons()
    {
        _addButton.Visible = false;
        _upgradeButton.Visible = false;
        _downgradeButton.Visible = false;
        _removeButton.Visible = false;
        if (_changing) return;

        // Only a reference the project makes itself can be moved or removed - not one a package brings in, or one the SDK adds
        var isDirect = ProjectPackageReference is { IsTopLevel: true, IsAutoReferenced: false };
        _removeButton.Visible = isDirect;

        if (VersionSelectedInDetails == null) return;

        if (isDirect is false)
        {
            _addButton.Visible = ProjectPackageReference?.IsAutoReferenced is not true;
            return;
        }

        var installed = ProjectPackageReference!.InstalledVersion;
        var selected = VersionSelectedInDetails;

        if (installed < selected)
        {
            _upgradeButton.Visible = true;
        }
        else if (installed > selected)
        {
            _downgradeButton.Visible = true;
        }
    }

    private void RequestChange(bool remove)
    {
        if (_changing || PackageId is not { } packageId) return;
        var version = VersionSelectedInDetails;
        if (remove is false && version is null) return;

        _changing = true;
        _installedVersionLabel.RemoveThemeColorOverride(ThemeStringNames.FontColor);
        _installedVersionLabel.Text = remove ? "Removing..." : $"Installing {version!.ToNormalizedString()}...";
        _installedVersionLabel.TooltipText = string.Empty;
        DisplayRelevantButtons();

        var project = ProjectModel;
        _ = Task.GodotRun(async () =>
        {
            var result = remove
                ? await _packageReferenceService.RemoveAsync(project, packageId)
                : await _packageReferenceService.AddOrUpdateAsync(project, packageId, version!);
            await this.InvokeAsync(() =>
            {
                _changing = false;
                if (result.Success)
                {
                    _installedVersionLabel.Text = string.Empty;
                }
                else
                {
                    _installedVersionLabel.Text = "Failed";
                    _installedVersionLabel.AddThemeColorOverride(ThemeStringNames.FontColor, ErrorColor);
                    _installedVersionLabel.TooltipText = result.Errors.Count > 1 ? string.Join('\n', result.Errors) : result.Error ?? "The change failed";
                }
                DisplayRelevantButtons();
            });
            if (result.Success && PackageReferencesChanged is { } changed) await changed();
        });
    }
}
