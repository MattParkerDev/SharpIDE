using System.Xml.Linq;
using NuGet.Versioning;

namespace SharpIDE.Application.Features.DotnetNew;

public static class SdkTemplateFolders
{
	public static IReadOnlyList<string> GetFolders(string sdkDirectory)
	{
		// SDK feature-band versions (10.0.402) differ from template/runtime versions
		// (10.0.13). Use the SDK's bundled runtime to cap the shared template folders.
		var props = XDocument.Load(Path.Combine(sdkDirectory, "Microsoft.NETCoreSdk.BundledVersions.props"));
		var version = props.Descendants().First(element => element.Name.LocalName == "BundledNETCoreAppPackageVersion").Value;
		var maximumVersion = NuGetVersion.Parse(version);
		var root = Path.Combine(Directory.GetParent(sdkDirectory)!.Parent!.FullName, "templates");
		var folders = new List<string>();
		if (Directory.Exists(root))
		{
			folders.AddRange(Directory.EnumerateDirectories(root)
				.Select(path => (Path: path, Version: NuGetVersion.TryParse(Path.GetFileName(path), out var parsed) ? parsed : null))
				.Where(entry => entry.Version is not null && entry.Version <= maximumVersion && (maximumVersion.IsPrerelease || !entry.Version.IsPrerelease))
				.GroupBy(entry => (entry.Version!.Major, entry.Version.Minor))
				.Select(group => group.OrderByDescending(entry => entry.Version).First())
				.OrderByDescending(entry => entry.Version)
				.Select(entry => entry.Path));
		}
		var localTemplates = Path.Combine(sdkDirectory, "Templates");
		if (Directory.Exists(localTemplates)) folders.Add(localTemplates);
		return folders;
	}
}
