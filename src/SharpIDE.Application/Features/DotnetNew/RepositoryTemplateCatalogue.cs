using Microsoft.TemplateEngine.Abstractions;
using NuGet.Versioning;

namespace SharpIDE.Application.Features.DotnetNew;

public sealed record SdkTemplateVariant(ITemplateInfo Template, DotnetTemplateSdk Sdk, bool IsSdkTemplate);

public sealed record RepositoryTemplateCatalogue(
	DotnetTemplateSdk RepositorySdk,
	Dictionary<string, Dictionary<string, List<SdkTemplateVariant>>> Categories)
{
	public static IReadOnlyList<DotnetTemplateSdk> EligibleSdks(DotnetTemplateSdk repositorySdk, IEnumerable<DotnetTemplateSdk> installed)
	{
		var ceiling = NuGetVersion.Parse(repositorySdk.Version);
		return installed
			.Where(sdk => NuGetVersion.Parse(sdk.Version) <= ceiling
				&& (ceiling.IsPrerelease || !NuGetVersion.Parse(sdk.Version).IsPrerelease))
			.OrderByDescending(sdk => NuGetVersion.Parse(sdk.Version))
			.ThenByDescending(sdk => sdk == repositorySdk)
			.ToArray();
	}
}
