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

	public async Task GetTemplates(CancellationToken cancellationToken = default)
	{
		var templateEngineHost = CliTemplateEngineHost.CreateHost(false, false, null, null, false, LogLevel.Information, _loggerFactory);
		var bootstrapper = new Bootstrapper(templateEngineHost, false);
		var templates = await bootstrapper.GetTemplatesAsync(cancellationToken);

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
