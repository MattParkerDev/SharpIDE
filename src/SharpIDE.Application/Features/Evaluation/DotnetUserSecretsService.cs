using Ardalis.GuardClauses;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Application.Features.Evaluation;

public class DotnetUserSecretsService(ProjectEvaluationService projectEvaluationService)
{
	private readonly ProjectEvaluationService _projectEvaluationService = projectEvaluationService;

	public async Task<(Guid, string filePath)> GetOrCreateUserSecretsId(SharpIdeProjectModel projectModel)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(DotnetUserSecretsService)}.{nameof(GetOrCreateUserSecretsId)}");
		Guard.Against.Null(projectModel, nameof(projectModel));

		var userSecretsId = await _projectEvaluationService.GetOrCreateUserSecretsIdAsync(projectModel).ConfigureAwait(false);
		var userSecretsFilePath = GetUserSecretsFilePath(userSecretsId);
		var file = new FileInfo(userSecretsFilePath);
		if (file.Exists)
		{
			return (userSecretsId, userSecretsFilePath);
		}
		var directory = file.Directory;
		if (directory!.Exists is false)
		{
			directory.Create();
		}
		await File.WriteAllTextAsync(userSecretsFilePath, "{}").ConfigureAwait(false);
		return (userSecretsId, userSecretsFilePath);
	}

	private static string GetUserSecretsFilePath(Guid userSecretsId)
	{
		var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var userSecretsPath = OperatingSystem.IsWindows() switch
		{
			true => Path.Combine(appDataPath, "Microsoft", "UserSecrets", userSecretsId.ToString(), "secrets.json"),
			false => Path.Combine(appDataPath, ".microsoft", "usersecrets", userSecretsId.ToString(), "secrets.json")
		};
		return userSecretsPath;
	}
}
