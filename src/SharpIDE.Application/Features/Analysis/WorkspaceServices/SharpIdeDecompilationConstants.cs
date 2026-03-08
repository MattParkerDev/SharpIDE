namespace SharpIDE.Application.Features.Analysis.WorkspaceServices;

public static class SharpIdeDecompilationConstants
{
	// TBD
	public static readonly string SymbolCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "SharpIdeSymbolCache");
}
