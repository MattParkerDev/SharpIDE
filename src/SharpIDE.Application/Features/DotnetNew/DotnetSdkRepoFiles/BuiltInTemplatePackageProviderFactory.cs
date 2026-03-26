// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.TemplatePackage;
// https://github.com/dotnet/sdk/blob/a13e02d05ec888660283ac7a5f0a48fb39185332/src/Cli/dotnet/Commands/New/BuiltInTemplatePackageProviderFactory.cs
namespace Microsoft.DotNet.Cli.Commands.New;

/// <summary>
/// Factory that is loaded by TemplateEngine via <see cref="IComponentManager"/>.
/// To create <see cref="ITemplatePackageProvider"/> which returns list of packages to be installed.
/// </summary>
internal class BuiltInTemplatePackageProviderFactory : ITemplatePackageProviderFactory
{
    public static readonly Guid FactoryId = new("{4B11226E-4594-43A4-B843-EB97447B6455}");

    public string DisplayName => ".NET SDK";

    public Guid Id { get => FactoryId; }

    public ITemplatePackageProvider CreateProvider(IEngineEnvironmentSettings settings)
        => new BuiltInTemplatePackageProvider(this, settings);
}
