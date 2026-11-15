using System.Text.Json;
using Ardalis.GuardClauses;

namespace SharpIDE.Godot.Features.IdeSettings;

public static class AppStateLoader
{
	private static string GetConfigFilePath()
	{
		var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var configFolder = Path.Combine(folder, "SharpIDE");
		Directory.CreateDirectory(configFolder);
		var configFilePath = Path.Combine(configFolder, "sharpIde.json");
		return configFilePath;
	}

	public static AppState LoadAppStateFromConfigFile()
	{
		var configFilePath = GetConfigFilePath();
		if (File.Exists(configFilePath) is false)
		{
			File.WriteAllText(configFilePath, "{}");
		}

		using var stream = File.OpenRead(configFilePath);
		var deserializedAppState = JsonSerializer.Deserialize<AppState>(stream);
		Guard.Against.Null(deserializedAppState, nameof(deserializedAppState));

		return deserializedAppState;
	}

	public static void SaveAppStateToConfigFile(AppState appState)
	{
		Guard.Against.Null(appState, nameof(appState));
		var configFilePath = GetConfigFilePath();
		using var stream = File.Create(configFilePath);
		JsonSerializer.Serialize(stream, appState);
		stream.Flush();
	}
}
