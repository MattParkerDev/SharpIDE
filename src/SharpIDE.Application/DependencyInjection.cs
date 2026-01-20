using Microsoft.Extensions.DependencyInjection;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Application.Features.Build;
using SharpIDE.Application.Features.Debugging;
using SharpIDE.Application.Features.Editor;
using SharpIDE.Application.Features.Evaluation;
using SharpIDE.Application.Features.FilePersistence;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.NavigationHistory;
using SharpIDE.Application.Features.Nuget;
using SharpIDE.Application.Features.Run;
using SharpIDE.Application.Features.Search;
using SharpIDE.Application.Features.Testing;

namespace SharpIDE.Application;

public static class DependencyInjection
{
	public static IServiceCollection AddApplication(this IServiceCollection services)
	{
		services.AddScoped<BuildService>();
		services.AddScoped<RunService>();
		services.AddScoped<DebuggingService>();
		services.AddScoped<SearchService>();
		services.AddScoped<IdeFileExternalChangeHandler>();
		services.AddScoped<IdeCodeActionService>();
		services.AddScoped<IdeRenameService>();
		services.AddScoped<IdeApplyCompletionService>();
		services.AddScoped<FileChangedService>();
		services.AddScoped<DotnetUserSecretsService>();
		services.AddScoped<NugetClientService>();
		services.AddScoped<TestRunnerService>();
		services.AddScoped<NugetPackageIconCacheService>();
		services.AddScoped<IdeFileWatcher>();
		services.AddScoped<IdeNavigationHistoryService>();
		services.AddScoped<IdeOpenTabsFileManager>();
		services.AddScoped<RoslynAnalysis>();
		services.AddScoped<IdeFileOperationsService>();
		services.AddScoped<SharpIdeSolutionModificationService>();
		services.AddScoped<AnalyzerFileWatcher>();
		services.AddScoped<EditorCaretPositionService>();
		services.AddLogging();
		return services;
	}
}
