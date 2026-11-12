using System.Xml.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost;

/// Adds, updates and removes package references by editing the MSBuild files themselves, then restoring.
/// Where a version goes is read from an evaluation of the project (in a ProjectCollection of its own, so the shared evaluations are untouched):
/// - with central package management: a VersionOverride the reference has, else the PackageVersion in force (in whichever file sets it), else a new PackageVersion in Directory.Packages.props
/// - without: the reference's Version, in whichever file sets it
/// - a version written as a single property ($(SomeVersion)) has that property set where the evaluation last set it, so the packages sharing it move together
/// The edits are made to a fresh, formatting-preserving load of each file, so everything else in the file is left as it was.
/// Restore is what checks the package fits (NU1202 when it doesn't support a target framework, NU1605 for a downgrade...) - when it fails, the files are put back.
public sealed class PackageReferenceEditor(Func<string, CancellationToken, Task<(bool Success, List<string> Errors)>> restoreAsync)
{
	private const string PackageReferenceItemType = "PackageReference";
	private const string PackageVersionItemType = "PackageVersion";
	private const string VersionMetadata = "Version";
	private const string VersionOverrideMetadata = "VersionOverride";

	private readonly SemaphoreSlim _lock = new(1, 1);

	public Task<PackageReferenceChangeResultDto> AddOrUpdateAsync(string projectFilePath, string packageId, string version, CancellationToken cancellationToken)
		=> ChangeAsync(projectFilePath, evaluations => PlanAddOrUpdate(evaluations, projectFilePath, packageId, version), addedPackage: (packageId, version), cancellationToken);

	public Task<PackageReferenceChangeResultDto> RemoveAsync(string projectFilePath, string packageId, CancellationToken cancellationToken)
		=> ChangeAsync(projectFilePath, evaluations => PlanRemove(evaluations, projectFilePath, packageId), addedPackage: null, cancellationToken);

	private async Task<PackageReferenceChangeResultDto> ChangeAsync(string projectFilePath, Func<ProjectEvaluations, Plan> plan, (string Id, string Version)? addedPackage, CancellationToken cancellationToken)
	{
		projectFilePath = Path.GetFullPath(projectFilePath);
		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		var originals = new Dictionary<string, byte[]>(PathComparer);
		try
		{
			Plan changePlan;
			try
			{
				using var evaluations = ProjectEvaluations.Load(projectFilePath);
				changePlan = plan(evaluations);
			}
			catch (InvalidProjectFileException ex)
			{
				return Failed($"The project could not be evaluated: {ex.Message}");
			}
			if (changePlan.Refusal is not null) return Failed(changePlan.Refusal);

			try
			{
				Apply(changePlan.Edits, originals);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidProjectFileException or InvalidOperationException)
			{
				PutBack(originals);
				return Failed($"The project files could not be written: {ex.Message}", originals);
			}

			var (restored, errors) = await restoreAsync(projectFilePath, cancellationToken).ConfigureAwait(false);
			if (restored && changePlan.AddsPackageReference && addedPackage is { } added && IsDevelopmentDependency(projectFilePath, added.Id, added.Version))
			{
				// Analyzers, source generators, build tooling - kept out of the packages that reference this project, as NuGet does on install
				Apply([new MarkDevelopmentDependency(projectFilePath, added.Id)], originals);
				(restored, errors) = await restoreAsync(projectFilePath, cancellationToken).ConfigureAwait(false);
			}
			if (restored is false)
			{
				PutBack(originals);
				// Put the assets file back in line with the restored project files
				await restoreAsync(projectFilePath, CancellationToken.None).ConfigureAwait(false);
				return new PackageReferenceChangeResultDto(false, errors.FirstOrDefault() ?? "Restore failed", originals.Keys.ToList(), errors);
			}
			return new PackageReferenceChangeResultDto(true, null, originals.Keys.ToList(), []);
		}
		catch (OperationCanceledException)
		{
			PutBack(originals);
			return Failed("Cancelled", originals);
		}
		catch (InvalidOperationException ex)
		{
			// e.g. BuildManager already has a build running
			PutBack(originals);
			return Failed(ex.Message, originals);
		}
		finally
		{
			_lock.Release();
		}
	}

	private static Plan PlanAddOrUpdate(ProjectEvaluations evaluations, string projectFilePath, string packageId, string version)
	{
		var references = evaluations.ItemsOf(PackageReferenceItemType, packageId);
		if (references.Any(IsImplicitlyDefined)) return Plan.Refuse($"{packageId} is referenced implicitly by the SDK, so its version isn't set by the project");

		var edits = new List<Edit>();
		var addsReference = references.Count is 0;
		if (evaluations.IsCentrallyManaged is false)
		{
			if (addsReference)
			{
				edits.Add(new AddItem(projectFilePath, PackageReferenceItemType, packageId, version));
				return Plan.Of(edits, addsReference);
			}
			foreach (var reference in references)
			{
				if (PlanVersionWrite(evaluations, reference, VersionMetadata, version, edits) is { } refusal) return Plan.Refuse(refusal);
			}
			return Plan.Of(edits, addsReference);
		}

		var overridden = references.Where(r => string.IsNullOrWhiteSpace(r.GetMetadataValue(VersionOverrideMetadata)) is false).ToList();
		if (overridden.Count > 0)
		{
			foreach (var reference in overridden)
			{
				if (PlanVersionWrite(evaluations, reference, VersionOverrideMetadata, version, edits) is { } refusal) return Plan.Refuse(refusal);
			}
			return Plan.Of(edits, addsReference);
		}

		if (addsReference) edits.Add(new AddItem(projectFilePath, PackageReferenceItemType, packageId, Version: null));
		var packageVersion = evaluations.ItemsOf(PackageVersionItemType, packageId).LastOrDefault();
		if (packageVersion is null)
		{
			var propsFilePath = evaluations.Outer.GetPropertyValue("DirectoryPackagesPropsPath");
			if (string.IsNullOrWhiteSpace(propsFilePath) || File.Exists(propsFilePath) is false)
			{
				return Plan.Refuse("The project manages package versions centrally, but its Directory.Packages.props could not be found");
			}
			edits.Add(new AddItem(Path.GetFullPath(propsFilePath), PackageVersionItemType, packageId, version));
			return Plan.Of(edits, addsReference);
		}
		if (PlanVersionWrite(evaluations, packageVersion, VersionMetadata, version, edits) is { } packageVersionRefusal) return Plan.Refuse(packageVersionRefusal);
		return Plan.Of(edits, addsReference);
	}

	private static Plan PlanRemove(ProjectEvaluations evaluations, string projectFilePath, string packageId)
	{
		var references = evaluations.ItemsOf(PackageReferenceItemType, packageId);
		if (references.Count is 0) return Plan.Refuse($"The project doesn't reference {packageId} directly");
		if (references.Any(IsImplicitlyDefined)) return Plan.Refuse($"{packageId} is referenced implicitly by the SDK, so it can't be removed from the project");

		var edits = new List<Edit>();
		foreach (var reference in references)
		{
			var definedIn = reference.Xml.ContainingProject.FullPath;
			if (PathComparer.Equals(definedIn, projectFilePath) is false)
			{
				return Plan.Refuse($"{packageId} is referenced in {Path.GetFileName(definedIn)}, which other projects may import - remove it there");
			}
			if (string.Equals(reference.Xml.Include.Trim(), packageId, StringComparison.OrdinalIgnoreCase) is false)
			{
				return Plan.Refuse($"{packageId} is referenced as Include=\"{reference.Xml.Include}\" alongside other packages - remove it by hand");
			}
			edits.Add(new RemoveItem(definedIn, At.Of(reference.Xml)));
		}
		return Plan.Of(edits, addsPackageReference: false);
	}

	/// Plans setting the version an item's metadata holds, where it is written: the metadata itself, or the property it names alone.
	/// Returns why not, if it can't be.
	private static string? PlanVersionWrite(ProjectEvaluations evaluations, ProjectItem item, string metadataName, string version, List<Edit> edits)
	{
		var metadata = item.GetMetadata(metadataName);
		if (metadata is null || metadata.Xml is null)
		{
			// No version at all - give the item one, where it is defined
			var itemFilePath = item.Xml.ContainingProject.FullPath;
			if (evaluations.IsEditable(itemFilePath) is false) return $"{item.EvaluatedInclude} is defined in {itemFilePath}, which isn't part of the solution";
			edits.Add(new SetMetadata(itemFilePath, At.Of(item.Xml), metadataName, version));
			return null;
		}

		var written = metadata.UnevaluatedValue.Trim();
		if (TryGetSinglePropertyName(written, out var propertyName))
		{
			var property = evaluations.PropertyFor(item, propertyName);
			if (property?.Xml is null || property.IsEnvironmentProperty || property.IsGlobalProperty || property.IsReservedProperty)
			{
				return $"{item.EvaluatedInclude}'s version is $({propertyName}), which no project file sets";
			}
			if (string.IsNullOrWhiteSpace(property.Xml.Condition) is false || string.IsNullOrWhiteSpace(property.Xml.Parent?.Condition) is false)
			{
				return $"{item.EvaluatedInclude}'s version is $({propertyName}), which is set under a condition in {Path.GetFileName(property.Xml.ContainingProject.FullPath)} - change it there";
			}
			if (IsExpression(property.UnevaluatedValue))
			{
				return $"{item.EvaluatedInclude}'s version is $({propertyName}), which is written as {property.UnevaluatedValue} - change it by hand";
			}
			var propertyFilePath = property.Xml.ContainingProject.FullPath;
			if (evaluations.IsEditable(propertyFilePath) is false) return $"{item.EvaluatedInclude}'s version is $({propertyName}), which is set in {propertyFilePath}, which isn't part of the solution";
			edits.Add(new SetProperty(propertyFilePath, At.Of(property.Xml), version));
			return null;
		}
		if (IsExpression(written))
		{
			return $"{item.EvaluatedInclude}'s version is written as {written} - change it by hand";
		}
		if (metadata.Xml.Parent is not ProjectItemElement itemElement)
		{
			return $"{item.EvaluatedInclude}'s version comes from an item definition - change it by hand";
		}
		if (string.IsNullOrWhiteSpace(metadata.Xml.Condition) is false)
		{
			return $"{item.EvaluatedInclude}'s version is set under a condition - change it by hand";
		}
		var metadataFilePath = metadata.Xml.ContainingProject.FullPath;
		if (evaluations.IsEditable(metadataFilePath) is false) return $"{item.EvaluatedInclude}'s version is set in {metadataFilePath}, which isn't part of the solution";
		edits.Add(new SetMetadata(metadataFilePath, At.Of(itemElement), metadataName, version));
		return null;
	}

	private static void Apply(IReadOnlyCollection<Edit> edits, Dictionary<string, byte[]> originals)
	{
		// A collection of its own, so each file is read fresh from disk, keeping its whitespace
		using var collection = new ProjectCollection();
		foreach (var fileEdits in edits.Distinct().GroupBy(e => e.FilePath, PathComparer))
		{
			var filePath = fileEdits.Key;
			if (originals.ContainsKey(filePath) is false) originals[filePath] = File.ReadAllBytes(filePath);
			var root = ProjectRootElement.Open(filePath, collection, preserveFormatting: true);
			foreach (var edit in fileEdits) edit.ApplyTo(root);
			if (root.HasUnsavedChanges) root.Save();
		}
		collection.UnloadAllProjects();
	}

	private static void PutBack(Dictionary<string, byte[]> originals)
	{
		foreach (var (filePath, bytes) in originals)
		{
			try
			{
				File.WriteAllBytes(filePath, bytes);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best effort - the IDE is told which files the change touched
			}
		}
	}

	/// developmentDependency in the package's nuspec, from the global packages folder restore put it in
	private static bool IsDevelopmentDependency(string projectFilePath, string packageId, string version)
	{
		try
		{
			using var evaluations = ProjectEvaluations.Load(projectFilePath);
			var packageRoot = evaluations.Outer.GetPropertyValue("NuGetPackageRoot");
			if (string.IsNullOrWhiteSpace(packageRoot)) return false;
			var id = packageId.ToLowerInvariant();
			var nuspecPath = Path.Combine(packageRoot, id, version.ToLowerInvariant(), $"{id}.nuspec");
			if (File.Exists(nuspecPath) is false) return false;
			var value = XDocument.Load(nuspecPath).Descendants().FirstOrDefault(e => e.Name.LocalName == "developmentDependency")?.Value;
			return string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is InvalidProjectFileException or IOException or System.Xml.XmlException)
		{
			return false;
		}
	}

	private static bool IsImplicitlyDefined(ProjectItem item) => string.Equals(item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase);

	private static bool IsExpression(string value) => value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal);

	private static bool TryGetSinglePropertyName(string value, out string name)
	{
		name = string.Empty;
		if (value.StartsWith("$(", StringComparison.Ordinal) is false || value.EndsWith(')') is false) return false;
		var inner = value[2..^1].Trim();
		if (inner.Length is 0 || inner.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') is false) return false;
		name = inner;
		return true;
	}

	private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private static PackageReferenceChangeResultDto Failed(string error, Dictionary<string, byte[]>? originals = null) => new(false, error, originals?.Keys.ToList() ?? [], []);

	private sealed record Plan(IReadOnlyList<Edit> Edits, bool AddsPackageReference, string? Refusal)
	{
		public static Plan Of(IReadOnlyList<Edit> edits, bool addsPackageReference) => new(edits, addsPackageReference, null);
		public static Plan Refuse(string why) => new([], false, why);
	}

	/// Where an element starts in its file - how an edit planned against one load of a file finds its element in another
	private readonly record struct At(int Line, int Column)
	{
		public static At Of(ProjectElement element) => new(element.Location.Line, element.Location.Column);

		public T Find<T>(ProjectRootElement root) where T : ProjectElement
		{
			var (line, column) = (Line, Column);
			return root.AllChildren.OfType<T>().FirstOrDefault(e => e.Location.Line == line && e.Location.Column == column)
			?? throw new InvalidOperationException($"{Path.GetFileName(root.FullPath)} changed on disk while the package change was being made");
		}
	}

	private abstract record Edit(string FilePath)
	{
		public abstract void ApplyTo(ProjectRootElement root);
	}

	private sealed record SetMetadata(string FilePath, At Item, string Name, string Value) : Edit(FilePath)
	{
		public override void ApplyTo(ProjectRootElement root)
		{
			var item = Item.Find<ProjectItemElement>(root);
			if (item.Metadata.FirstOrDefault(m => string.Equals(m.Name, Name, StringComparison.OrdinalIgnoreCase)) is { } metadata)
			{
				metadata.Value = Value; // Kept as an attribute or element, whichever it was
			}
			else
			{
				item.AddMetadata(Name, Value, expressAsAttribute: true);
			}
		}
	}

	private sealed record SetProperty(string FilePath, At Property, string Value) : Edit(FilePath)
	{
		public override void ApplyTo(ProjectRootElement root) => Property.Find<ProjectPropertyElement>(root).Value = Value;
	}

	private sealed record AddItem(string FilePath, string ItemType, string Include, string? Version) : Edit(FilePath)
	{
		public override void ApplyTo(ProjectRootElement root)
		{
			// Beside the file's other items of the type, else in an item group of its own
			var itemGroup = root.ItemGroups.FirstOrDefault(g => g.Parent == root && string.IsNullOrWhiteSpace(g.Condition) && g.Items.Any(i => i.ItemType == ItemType))
				?? root.AddItemGroup();
			var item = itemGroup.AddItem(ItemType, Include);
			if (Version is not null) item.AddMetadata(VersionMetadata, Version, expressAsAttribute: true);
		}
	}

	private sealed record RemoveItem(string FilePath, At Item) : Edit(FilePath)
	{
		public override void ApplyTo(ProjectRootElement root)
		{
			var item = Item.Find<ProjectItemElement>(root);
			var parent = item.Parent;
			parent.RemoveChild(item);
			// Take the group it leaves empty too
			if (parent is ProjectItemGroupElement { Count: 0 } emptyGroup) emptyGroup.Parent.RemoveChild(emptyGroup);
		}
	}

	private sealed record MarkDevelopmentDependency(string FilePath, string PackageId) : Edit(FilePath)
	{
		public override void ApplyTo(ProjectRootElement root)
		{
			var item = root.Items.LastOrDefault(i => i.ItemType == PackageReferenceItemType && string.Equals(i.Include.Trim(), PackageId, StringComparison.OrdinalIgnoreCase));
			if (item is null || item.Metadata.Any(m => m.Name is "PrivateAssets" or "IncludeAssets")) return;
			item.AddMetadata("PrivateAssets", "all", expressAsAttribute: false);
			item.AddMetadata("IncludeAssets", "runtime; build; native; contentfiles; analyzers; buildtransitive", expressAsAttribute: false);
		}
	}

	/// The project's outer evaluation, and one per target framework when it has several - a reference may be conditioned on one
	private sealed class ProjectEvaluations : IDisposable
	{
		private readonly ProjectCollection _collection;
		private readonly List<Project> _innerProjects;
		private readonly string[] _nonEditableRoots;

		public Project Outer { get; }
		public bool IsCentrallyManaged { get; }

		private ProjectEvaluations(ProjectCollection collection, Project outer, List<Project> innerProjects)
		{
			_collection = collection;
			Outer = outer;
			_innerProjects = innerProjects;
			IsCentrallyManaged = string.Equals(outer.GetPropertyValue("ManagePackageVersionsCentrally"), "true", StringComparison.OrdinalIgnoreCase);
			_nonEditableRoots = new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildBinPath", "NuGetPackageRoot", "NetCoreRoot", "DOTNET_HOST_PATH" }
				.Select(outer.GetPropertyValue)
				.Where(p => string.IsNullOrWhiteSpace(p) is false)
				.Select(p => Path.GetFullPath(File.Exists(p) ? Path.GetDirectoryName(p)! : p))
				.ToArray();
		}

		public static ProjectEvaluations Load(string projectFilePath)
		{
			var collection = new ProjectCollection();
			try
			{
				var outer = collection.LoadProject(projectFilePath);
				var innerProjects = outer.GetPropertyValue("TargetFrameworks")
					.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Select(tfm => collection.LoadProject(projectFilePath, new Dictionary<string, string> { ["TargetFramework"] = tfm }, toolsVersion: null))
					.ToList();
				return new ProjectEvaluations(collection, outer, innerProjects);
			}
			catch
			{
				collection.Dispose();
				throw;
			}
		}

		private IEnumerable<Project> All => _innerProjects.Count is 0 ? [Outer] : [Outer, .._innerProjects];

		/// The items of the type for the package across the evaluations, one per element that defines them
		public List<ProjectItem> ItemsOf(string itemType, string packageId) => All
			.SelectMany(p => p.GetItems(itemType))
			.Where(i => string.Equals(i.EvaluatedInclude, packageId, StringComparison.OrdinalIgnoreCase))
			.DistinctBy(i => (i.Xml.ContainingProject.FullPath, i.Xml.Location.Line, i.Xml.Location.Column))
			.ToList();

		public ProjectProperty? PropertyFor(ProjectItem item, string name) => item.Project.GetProperty(name);

		/// Not a file of the SDK, MSBuild, or a package
		public bool IsEditable(string filePath) => File.Exists(filePath) && _nonEditableRoots.Any(root => IsUnder(filePath, root)) is false;

		private static bool IsUnder(string filePath, string directoryPath)
		{
			var relative = Path.GetRelativePath(directoryPath, filePath);
			return relative.StartsWith("..", StringComparison.Ordinal) is false && Path.IsPathRooted(relative) is false;
		}

		public void Dispose()
		{
			_collection.UnloadAllProjects();
			_collection.Dispose();
		}
	}
}
