using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Photino.Blazor;
using SharpIDE.Application;
using SharpIDE.Photino.Models;
using SharpIDE.Photino.Services;

namespace SharpIDE.Photino;

// This C# project is deprecated and serves as a reference only - see the SharpIDE.Godot project
public class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		throw new NotSupportedException("This project is deprecated and serves as a reference only - see the SharpIDE.Godot project");
		var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

		appBuilder.Services.AddLogging();//
		appBuilder.Services.AddMudServices();

		appBuilder.Services.AddApplication();
		appBuilder.Services.AddSingleton<RefreshOpenFileService>();
		appBuilder.Services.AddSingleton<AppState>();

		appBuilder.RootComponents.Add<App>("app");

		var app = appBuilder.Build();

		app.MainWindow
			.SetSize(1400, 800)
			.SetDevToolsEnabled(true)
			//.SetMaximized(true)
			.SetLogVerbosity(0)
			//.SetLocation(new Point(2560, 0)) // This will open the window at the specified location
			//.SetUseOsDefaultLocation(true) // This will open the window on the same monitor as where the exe is launched from
			//.SetIconFile("favicon.ico")
			.SetTitle("SharpIDE.Photino");

		app.MainWindow.WindowFocusInHandler += (sender, _) =>
		{
			var refreshOpenFileService = app.Services.GetRequiredService<RefreshOpenFileService>();
			refreshOpenFileService.InvokeRefreshOpenFile();
		};

		var configFilePath = GetConfigFilePath();

		using var scope = app.Services.CreateScope();
		var appState = scope.ServiceProvider.GetRequiredService<AppState>();

		LoadAppStateFromConfigFile(appState, configFilePath);

		app.MainWindow.RegisterWindowClosingHandler((sender, eventArgs) =>
		{
			using var stream = File.Create(configFilePath);
			JsonSerializer.Serialize(stream, appState);
			stream.Flush();
			return false;
		});

		AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
		{
			app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString());
		};

		// MSBuild runs out of process, in SharpIDE.MsBuildHost - it is never registered or loaded here

		app.Run();
	}

	private static string GetConfigFilePath()
	{
		var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var configFolder = Path.Combine(folder, "SharpIDE.PhotinoConfig");
		Directory.CreateDirectory(configFolder);
		var configFilePath = Path.Combine(configFolder, "config.json");
		return configFilePath;
	}

	private static void LoadAppStateFromConfigFile(AppState appState, string configFilePath)
	{
		if (File.Exists(configFilePath) is false)
		{
			File.WriteAllText(configFilePath, string.Empty);
		}

		using var stream = File.OpenRead(configFilePath);
		if (stream.Length is 0)
		{
			return;
		}
		var deserializedAppState = JsonSerializer.Deserialize<AppState>(stream);
		if (deserializedAppState is not null)
		{
			appState.SolutionFilePath = deserializedAppState.SolutionFilePath;
			appState.IdeSettings = deserializedAppState.IdeSettings;
		}
	}
}
