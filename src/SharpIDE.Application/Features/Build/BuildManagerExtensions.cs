using Microsoft.Build.Execution;

namespace SharpIDE.Application.Features.Build;

public static class BuildManagerExtensions
{
	/// <summary>
	/// Convenience method. Submits a lone build request and returns a Task that will complete when results are available.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown if a build is already in progress.</exception>
	public static async Task<BuildResult> BuildAsync(this BuildManager buildManager, BuildParameters parameters, BuildRequestData requestData, CancellationToken cancellationToken = default)
	{
		BuildResult result;
		buildManager.BeginBuild(parameters);
		try
		{
			var tcs = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			await using var _ = cancellationToken.Register(() => buildManager.CancelAllSubmissions()).ConfigureAwait(false);

			try
			{
				var buildSubmission = buildManager.PendBuildRequest(requestData);
				buildSubmission.ExecuteAsync(sub =>
				{
					var buildResult = sub.BuildResult!;

					tcs.SetResult(buildResult);
				}, null);
			}
			catch (Exception ex)
			{
				tcs.SetException(ex);
			}

			result = await tcs.Task.ConfigureAwait(false);
		}
		finally
		{
			buildManager.EndBuild();
		}
		return result;
	}
}
