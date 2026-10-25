// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.TemplatePackage;
using SharpIDE.Application.Features.DotnetNew;

namespace Microsoft.DotNet.Cli.Commands.New;

internal sealed class BuiltInTemplatePackageProvider(BuiltInTemplatePackageProviderFactory factory, string sdkDirectory) : ITemplatePackageProvider
{
    public ITemplatePackageProviderFactory Factory { get; } = factory;

#pragma warning disable CS0067
    public event Action? TemplatePackagesChanged;
#pragma warning restore CS0067

    public Task<IReadOnlyList<ITemplatePackage>> GetAllTemplatePackagesAsync(CancellationToken cancellationToken)
    {
        var packages = new List<ITemplatePackage>();
        foreach (var folder in SdkTemplateFolders.GetFolders(sdkDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.nupkg"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                packages.Add(new TemplatePackage(this, path, File.GetLastWriteTime(path)));
            }
        }
        return Task.FromResult<IReadOnlyList<ITemplatePackage>>(packages);
    }
}
