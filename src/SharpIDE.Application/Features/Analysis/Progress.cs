using Microsoft.CodeAnalysis.MSBuild;

namespace SharpIDE.Application.Features.Analysis;

public class Progress : IProgress<ProjectLoadProgress>
{
	public void Report(ProjectLoadProgress value)
	{
		//Console.WriteLine($"{value.Operation} completed for {value.FilePath} ({value.TargetFramework}) in {value.ElapsedTime.TotalMilliseconds}ms");
	}
}
