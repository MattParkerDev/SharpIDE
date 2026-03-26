using Microsoft.DotNet.Cli.Commands.New;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.TemplatePackage;
using Microsoft.TemplateEngine.Cli;
using Microsoft.TemplateEngine.IDE;

namespace SharpIDE.Application.Features.DotnetNew;

public class DotnetTemplateService(ILoggerFactory loggerFactory)
{
	private readonly ILoggerFactory _loggerFactory = loggerFactory;

	public async Task<Dictionary<string, Dictionary<string, List<ITemplateInfo>>>> GetCategorisedTemplates(CancellationToken cancellationToken = default)
	{
		var templates = await GetTemplates(cancellationToken);
		var categories = new HashSet<string>(["Web", "WinForms", "WPF", "Console", "Library", "Test", "Aspire"], StringComparer.OrdinalIgnoreCase);

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
		return result;
	}

	public async Task<IReadOnlyList<ITemplateInfo>> GetTemplates(CancellationToken cancellationToken = default)
	{
		var templateEngineHost = CliTemplateEngineHost.CreateHost(false, false, null, null, false, LogLevel.Information, _loggerFactory);
		var bootstrapper = new Bootstrapper(templateEngineHost, false);
		var templates = await bootstrapper.GetTemplatesAsync(cancellationToken);

		return templates;

		// Console.WriteLine($"Found {templates.Count} templates");
		// foreach (var template in templates)
		// {
		// 	Console.WriteLine($"Template package: {template.Name} ({template.Identity})");
		// }

		// run a template
		// var template = templates.First();
		// var path = @$"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\Temp\SharpIdeTestApp";
		// var name = Path.GetFileName(path);
		// var templateCreator = await bootstrapper.CreateAsync(template, name, path, (Dictionary<string, string?>)[], null, cancellationToken);
		;
	}
}
