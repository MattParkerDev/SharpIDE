using Microsoft.CodeAnalysis;


namespace SharpIDE.Application.Features.Analysis;

internal static class AssemblyMetadataExtensions
{
	public static Guid GetMvid(this AssemblyMetadata assemblyMetadata)
	{
		var module = assemblyMetadata.GetModules()[0];
		var reader = module.GetMetadataReader();

		var moduleDef = reader.GetModuleDefinition();
		var mvid = reader.GetGuid(moduleDef.Mvid);
		return mvid;
	}
}
