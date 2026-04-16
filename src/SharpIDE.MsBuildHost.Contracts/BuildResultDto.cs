namespace SharpIDE.MsBuildHost.Contracts;

public enum BuildTypeDto
{
	Build,
	Rebuild,
	Clean,
	Restore
}
public enum BuildResultDto { Success = 0, Failure }
