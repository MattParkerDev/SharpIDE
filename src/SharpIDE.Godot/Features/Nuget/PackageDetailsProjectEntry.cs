using Godot;
using NuGet.Versioning;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Nuget;

public partial class PackageDetailsProjectEntry : MarginContainer
{
    private Label _projectNameLabel = null!;
    private Label _installedVersionLabel = null!;
    private Button _addButton = null!;
    private Button _upgradeButton = null!;
    private Button _downgradeButton = null!;
    private Button _removeButton = null!;
    
    public SharpIdeProjectModel ProjectModel { get; set; } = null!;
    public ProjectPackageReference? ProjectPackageReference { get; set; }
    public NuGetVersion? VersionSelectedInDetails { get; set; }
    public override void _Ready()
    {
        _projectNameLabel = GetNode<Label>("%ProjectNameLabel");;
        _installedVersionLabel = GetNode<Label>("%InstalledVersionLabel");
        _addButton = GetNode<Button>("%AddButton");
        _upgradeButton = GetNode<Button>("%UpgradeButton");
        _downgradeButton = GetNode<Button>("%DowngradeButton");
        _removeButton = GetNode<Button>("%RemoveButton");
        _installedVersionLabel.Text = string.Empty;
        SetValues();
    }
    
    public void SetValues()
    {
        if (ProjectModel == null) return;
        _projectNameLabel.Text = ProjectModel.Name.Value;
        if (ProjectPackageReference == null) return;
        var isTransitive = ProjectPackageReference.IsTransitive;
        var installedVersion = ProjectPackageReference.InstalledVersion;
        _installedVersionLabel.Text = isTransitive ? $"({installedVersion?.ToNormalizedString()})" : installedVersion?.ToNormalizedString();
        
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
                                                  {string.Join("\n", transitiveOriginsGroupedByVersion.Select(t => $"{t.RequestedVersion.ToString("p", VersionRangeFormatter.Instance)} by {string.Join(", ", t.PackageNames)}"))}
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
        
        if (VersionSelectedInDetails == null)
        {
            _removeButton.Visible = true;
            return;
        }

        if (ProjectPackageReference == null || ProjectPackageReference.IsTransitive)
        {
            _addButton.Visible = true;
            return;
        }

        var installed = ProjectPackageReference.InstalledVersion;
        var selected = VersionSelectedInDetails;

        if (installed < selected)
        {
            _upgradeButton.Visible = true;
        }
        else if (installed > selected)
        {
            _downgradeButton.Visible = true;
        }

        _removeButton.Visible = true;
    }
}