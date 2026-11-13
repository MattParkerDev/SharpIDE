using System.Globalization;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using SharpIDE.MsBuildHost.Contracts;

namespace SharpIDE.MsBuildHost.DesignTime;

// Adapted from Roslyn's BuildHost Extensions and Conversions
// https://github.com/dotnet/roslyn/tree/main/src/Workspaces/MSBuild/BuildHost/MSBuild/ProjectFile
internal static class ProjectInstanceExtensions
{
	public static IEnumerable<ITaskItem> GetAdditionalFiles(this ProjectInstance project) => project.GetItems("AdditionalFiles");
	public static IEnumerable<ITaskItem> GetAnalyzers(this ProjectInstance project) => project.GetItems("Analyzer");
	public static IEnumerable<ITaskItem> GetDocuments(this ProjectInstance project) => project.GetItems("Compile");
	public static IEnumerable<ITaskItem> GetEditorConfigFiles(this ProjectInstance project) => project.GetItems("EditorConfigFiles");

	/// projectDirectory, rather than the instance's FullPath - a project's state after building on an out of proc node doesn't have it
	public static IEnumerable<MetadataReferenceItemDto> GetMetadataReferences(this ProjectInstance project, string projectDirectory)
	{
		foreach (var item in project.GetItems("ReferencePath"))
		{
			if (item.ReferenceOutputAssemblyIsTrue())
			{
				yield return new MetadataReferenceItemDto(ResolvePath(item.EvaluatedInclude, projectDirectory), item.GetAliases(), EmbedInteropTypes: ToBool(item.GetMetadataValue("EmbedInteropTypes")));
			}
		}
	}

	/// Resolves a path that may be relative to the directory, normalising it - or leaves it as it is, if it can't be
	public static string ResolvePath(string path, string baseDirectory)
	{
		try
		{
			return Path.GetFullPath(path, baseDirectory);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return path;
		}
	}

	public static IEnumerable<ProjectFileReferenceDto> GetProjectReferences(this ProjectInstance project) => project
		.GetItems("ProjectReference")
		.Select(reference => new ProjectFileReferenceDto(reference.EvaluatedInclude, reference.GetAliases(), reference.ReferenceOutputAssemblyIsTrue()));

	public static PackageReferenceItemDto[] GetPackageReferences(this ProjectInstance project) => project
		.GetItems("PackageReference")
		.Select(item => new PackageReferenceItemDto(item.EvaluatedInclude, item.GetMetadataValue("Version")))
		.Distinct()
		.ToArray();

	public static string[] GetAliases(this ITaskItem item)
	{
		var aliasesText = item.GetMetadata("Aliases");
		return string.IsNullOrWhiteSpace(aliasesText) ? [] : [.. aliasesText.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim())];
	}

	public static bool ReferenceOutputAssemblyIsTrue(this ITaskItem item)
	{
		var referenceOutputAssemblyText = item.GetMetadata("ReferenceOutputAssembly");
		return string.IsNullOrWhiteSpace(referenceOutputAssemblyText) || string.Equals(referenceOutputAssemblyText, bool.FalseString, StringComparison.OrdinalIgnoreCase) is false;
	}

	public static string? ReadPropertyString(this ProjectInstance project, string propertyName) => project.GetProperty(propertyName)?.EvaluatedValue;
	public static bool ReadPropertyBool(this ProjectInstance project, string propertyName) => ToBool(project.ReadPropertyString(propertyName));
	public static int ReadPropertyInt(this ProjectInstance project, string propertyName) => int.TryParse(project.ReadPropertyString(propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
	public static int ReadCodePage(this ProjectInstance project) => project.ReadPropertyInt("CodePage") is >= 0 and var codePage ? codePage : 0;

	public static string ReadItemsAsString(this ProjectInstance project, string itemType) => string.Join(" ", project.GetItems(itemType).Select(static i => i.EvaluatedInclude));

	public static bool ToBool(string? value) => value is not null
		&& (string.Equals(bool.TrueString, value, StringComparison.OrdinalIgnoreCase) || string.Equals("On", value, StringComparison.OrdinalIgnoreCase));
}
