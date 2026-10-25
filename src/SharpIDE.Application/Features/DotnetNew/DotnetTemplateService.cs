using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Cli;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.IDE;

namespace SharpIDE.Application.Features.DotnetNew;

public class DotnetTemplateService(ILoggerFactory loggerFactory)
{
	private readonly ILoggerFactory _loggerFactory = loggerFactory;
	public static HashSet<string> MicrosoftTemplateCategories { get; } = new HashSet<string>(["Console", "Library", "Web", "WinForms", "WPF", "Test", "Aspire"], StringComparer.OrdinalIgnoreCase);
	private readonly System.Collections.Concurrent.ConcurrentDictionary<(string MountPoint, string ConfigPlace), TemplateParameterVisibility> _parameterVisibility = new();

	public bool IsParameterVisible(ITemplateInfo template, string parameterName)
	{
		return !_parameterVisibility.TryGetValue((template.MountPointUri, template.ConfigPlace), out var visibility)
			|| visibility.IsVisible(parameterName);
	}

	public async Task<RepositoryTemplateCatalogue> GetRepositoryCatalogue(string workingDirectory, CancellationToken cancellationToken = default)
	{
		var installed = await SharpIdeDotnetSdkDirectoryLocator.GetInstalledSdks(workingDirectory, cancellationToken);
		var repositorySdk = await SharpIdeDotnetSdkDirectoryLocator.ResolveSdk(workingDirectory, installed, cancellationToken);
		Dictionary<string, Dictionary<string, List<SdkTemplateVariant>>> categories = [];
		foreach (var sdk in RepositoryTemplateCatalogue.EligibleSdks(repositorySdk, installed))
		{
			var catalogue = await GetCategorisedTemplates(sdk, workingDirectory, cancellationToken);
			var sdkFolders = SdkTemplateFolders.GetFolders(sdk.Directory).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
			foreach (var (category, groups) in catalogue)
			{
				foreach (var (identity, templates) in groups)
				{
					// A custom package is not an SDK release. Include it once, with the
					// repository SDK as its execution context, rather than duplicating it.
					var candidates = templates.Select(template => new SdkTemplateVariant(template, sdk,
						sdkFolders.Contains(Path.GetDirectoryName(template.MountPointUri) ?? string.Empty)))
						.Where(variant => variant.IsSdkTemplate || sdk == repositorySdk)
						.OrderByDescending(variant => variant.Template.Precedence);
					var selected = candidates.FirstOrDefault();
					if (selected is null) continue;
					if (!categories.TryGetValue(category, out var targetGroups)) categories[category] = targetGroups = [];
					if (!targetGroups.TryGetValue(identity, out var variants)) targetGroups[identity] = variants = [];
					variants.Add(selected);
				}
			}
		}
		return new RepositoryTemplateCatalogue(repositorySdk, categories);
	}

	public async Task<Dictionary<string, Dictionary<string, List<ITemplateInfo>>>> GetCategorisedTemplates(DotnetTemplateSdk sdk, string workingDirectory, CancellationToken cancellationToken = default)
	{
		using var bootstrapper = CreateBootstrapper(sdk, workingDirectory);
		var templates = await bootstrapper.GetTemplatesAsync(cancellationToken);
		var categories = MicrosoftTemplateCategories;

		var categorizedTemplates = templates
			.Where(t =>
				t.TagsCollection.GetValueOrDefault("type") == "project" &&
				t.TagsCollection.GetValueOrDefault("language") == "C#")
			.Select(t => new
			{
				Template = t,
				Category = t.Author == "Microsoft"
					? t.Classifications.FirstOrDefault(c => categories.Contains(c)) ?? "Custom"
					: "Custom"
			});

		var result = categorizedTemplates
			.GroupBy(x => x.Category)
			.ToDictionary(
				g => g.Key,
				g => g.GroupBy(x => x.Template.GroupIdentity ?? x.Template.Identity)
					.ToDictionary(
						gg => gg.Key,
						gg => gg.Select(x => x.Template).ToList()
					)
			);
		// Load metadata off the UI thread along with the template catalogue.
		foreach (var template in result.Values.SelectMany(groups => groups.Values).SelectMany(templatesInGroup => templatesInGroup))
		{
			cancellationToken.ThrowIfCancellationRequested();
			_parameterVisibility[(template.MountPointUri, template.ConfigPlace)] = TemplateParameterVisibility.Load(
				template, _loggerFactory.CreateLogger<DotnetTemplateService>());
		}
		return result;
	}

	/// <summary>
	/// <paramref name="path"/> must include the project folder, ie the <paramref name="projectName"/> is not appended to it by the Template Engine
	/// </summary>
	public async Task<ITemplateCreationResult> ExecuteTemplate(DotnetTemplateSdk sdk, ITemplateInfo template, string projectName, string path, Dictionary<string, string?> parameters, CancellationToken cancellationToken = default)
	{
		using var bootstrapper = CreateBootstrapper(sdk, path);
		return await bootstrapper.CreateAsync(template, projectName, path, parameters, cancellationToken: cancellationToken);
	}

	private Bootstrapper CreateBootstrapper(DotnetTemplateSdk sdk, string workingDirectory)
	{
		var host = CliTemplateEngineHost.CreateHost(false, false, null, new FileInfo(workingDirectory), false,
			LogLevel.Information, new BorrowedLoggerFactory(_loggerFactory), sdk.Directory);
		// Each host gets an isolated in-memory catalogue, but global packages.json and
		// installed packages must stay on the physical filesystem for discovery and creation.
		var paths = new DefaultPathInfo(new DefaultEnvironment(), host);
		host.VirtualizeDirectory(paths.HostSettingsDir);
		return new Bootstrapper(host, virtualizeConfiguration: false, loadDefaultComponents: false);
	}

	// Bootstrapper disposes its host; the application still owns the logger factory.
	private sealed class BorrowedLoggerFactory(ILoggerFactory inner) : ILoggerFactory
	{
		public ILogger CreateLogger(string categoryName) => inner.CreateLogger(categoryName);
		public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
		public void Dispose() { }
	}
}
