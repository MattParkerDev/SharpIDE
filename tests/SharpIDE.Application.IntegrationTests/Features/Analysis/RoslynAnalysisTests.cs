using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.DotnetNew;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

[assembly: CaptureConsole]

namespace SharpIDE.Application.IntegrationTests.Features.Analysis;
public class RoslynAnalysisTests
{
	private readonly ITestOutputHelper _testOutputHelper;

	public RoslynAnalysisTests(ITestOutputHelper testOutputHelper)
	{
		_testOutputHelper = testOutputHelper;
		SharpIdeMsbuildLocator.Register();
	}


	[Fact]
    public async Task GetProjectDiagnostics_NoSolutionChanges_IsSubsequentlyCheaper()
    {
	    // Arrange
	    var serviceCollection = new ServiceCollection();
	    serviceCollection.AddApplication();

	    var services = serviceCollection.BuildServiceProvider();
	    var logger = services.GetRequiredService<ILogger<RoslynAnalysis>>();
	    var buildService = services.GetRequiredService<BuildService>();
	    var analyzerFileWatcher = services.GetRequiredService<AnalyzerFileWatcher>();
	    var vsPersistenceSolutionService = services.GetRequiredService<VsPersistenceSolutionService>();

	    var roslynAnalysis = new RoslynAnalysis(logger, buildService, analyzerFileWatcher);

	    var solutionModel = await vsPersistenceSolutionService.GetSolutionModel(@"C:\Users\Matthew\Documents\Git\SharpIDE\SharpIDE.slnx", TestContext.Current.CancellationToken);
	    var sharpIdeApplicationProject = solutionModel.AllProjects.Single(p => p.Name.Value == "SharpIDE.Application");

	    var timer = Stopwatch.StartNew();
		roslynAnalysis._solutionLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	    await roslynAnalysis.LoadSolutionInWorkspace(solutionModel, TestContext.Current.CancellationToken);
	    timer.Stop();
	    _testOutputHelper.WriteLine($"Solution load: {timer.ElapsedMilliseconds} ms");

	    // Act
	    foreach (var i in Enumerable.Range(0, 3))
	    {
		    timer.Restart();
		    await roslynAnalysis.GetProjectDiagnostics(sharpIdeApplicationProject, TestContext.Current.CancellationToken);
		    timer.Stop();
		    _testOutputHelper.WriteLine($"Diagnostics: {timer.ElapsedMilliseconds.ToString()}ms");
	    }
    }
}
