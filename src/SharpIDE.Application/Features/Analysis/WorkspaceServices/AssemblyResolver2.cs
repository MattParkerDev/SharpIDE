// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;

namespace SharpIDE.Application.Features.Analysis.WorkspaceServices;

// Copied from roslyn due to MethodMissingException using latest ICSharpCode.Decompiler
internal sealed class AssemblyResolver2 : IAssemblyResolver
{
    private static readonly Dictionary<MetadataReference, (string fileName, ImmutableArray<byte> image)> _inMemoryImagesForTesting = [];

    private readonly Compilation _parentCompilation;
    private readonly Dictionary<string, List<IAssemblySymbol>> _cache = [];
    private readonly StringBuilder _logger;

    public AssemblyResolver2(Compilation parentCompilation, StringBuilder logger)
    {
        _parentCompilation = parentCompilation;
        _logger = logger;
        BuildReferenceCache();
        Log(FeaturesResources._0_items_in_cache, _cache.Count);

        void BuildReferenceCache()
        {
            foreach (var reference in _parentCompilation.GetReferencedAssemblySymbols())
            {
                if (!_cache.TryGetValue(reference.Identity.Name, out var list))
                {
                    list = [];
                    _cache.Add(reference.Identity.Name, list);
                }

                list.Add(reference);
            }
        }
    }

    public async Task<MetadataFile> ResolveAsync(IAssemblyReference name)
    {
        return Resolve(name);
    }

    public async Task<MetadataFile> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
    {
        return ResolveModule(mainModule, moduleName);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Could be non-static if instance data is accessed")]
    public PEFile TryResolve(MetadataReference metadataReference, PEStreamOptions streamOptions)
    {
	    // Overriden as this method in roslyn calls a PEFile ctor which no longer exists in latest ICSharpCode.Decompiler
	    return null;
    }

    public MetadataFile Resolve(IAssemblyReference name)
    {
        Log("------------------");
        Log(FeaturesResources.Resolve_0, name.FullName);

        // First, find the correct list of assemblies by name
        if (!_cache.TryGetValue(name.Name, out var assemblies))
        {
            Log(FeaturesResources.Could_not_find_by_name_0, name.FullName);
            return null;
        }

        // If we have only one assembly available, just use it.
        // This is necessary, because in most cases there is only one assembly,
        // but still might have a version different from what the decompiler asks for.
        if (assemblies.Count == 1)
        {
            Log(FeaturesResources.Found_single_assembly_0, assemblies[0]);
            if (assemblies[0].Identity.Version != name.Version)
            {
                Log(FeaturesResources.WARN_Version_mismatch_Expected_0_Got_1, name.Version, assemblies[0].Identity.Version);
            }

            return MakePEFile(assemblies[0]);
        }

        // There are multiple assemblies
        Log(FeaturesResources.Found_0_assemblies_for_1, assemblies.Count, name.Name);

        // Get an exact match or highest version match from the list
        IAssemblySymbol highestVersion = null;
        IAssemblySymbol exactMatch = null;

        var publicKeyTokenOfName = name.PublicKeyToken ?? [];

        foreach (var assembly in assemblies)
        {
            Log(assembly.Identity.GetDisplayName());
            var version = assembly.Identity.Version;
            var publicKeyToken = assembly.Identity.PublicKey;
            if (version == name.Version && publicKeyToken.SequenceEqual(publicKeyTokenOfName))
            {
                exactMatch = assembly;
                Log(FeaturesResources.Found_exact_match_0, assembly);
            }
            else if (highestVersion == null || highestVersion.Identity.Version < version)
            {
                highestVersion = assembly;
                Log(FeaturesResources.Found_higher_version_match_0, assembly);
            }
        }

        var chosen = exactMatch ?? highestVersion;
        Log(FeaturesResources.Chosen_version_0, chosen);
        return MakePEFile(chosen);

        PEFile MakePEFile(IAssemblySymbol assembly)
        {
            // reference assemblies should be fine here, we only need the metadata of references.
            var reference = _parentCompilation.GetMetadataReference(assembly);
            Log(FeaturesResources.Load_from_0, reference.Display);

            var result = TryResolve(reference, PEStreamOptions.PrefetchMetadata);
            if (result is not null)
            {
                return result;
            }

            if (File.Exists(reference.Display))
            {
                return new PEFile(reference.Display, PEStreamOptions.PrefetchMetadata);
            }

            return null;
        }
    }

    public MetadataFile ResolveModule(MetadataFile mainModule, string moduleName)
    {
        Log("-------------");
        Log(FeaturesResources.Resolve_module_0_of_1, moduleName, mainModule.FullName);

        // Primitive implementation to support multi-module assemblies
        // where all modules are located next to the main module.
        var baseDirectory = Path.GetDirectoryName(mainModule.FileName);
        var moduleFileName = Path.Combine(baseDirectory, moduleName);
        if (!File.Exists(moduleFileName))
        {
            Log(FeaturesResources.Module_not_found);
            return null;
        }

        Log(FeaturesResources.Load_from_0, moduleFileName);
        return new PEFile(moduleFileName, PEStreamOptions.PrefetchMetadata);
    }

    private void Log(string format, params object[] args)
        => _logger.AppendFormat(format + Environment.NewLine, args);

    internal static class TestAccessor
    {
        public static void AddInMemoryImage(MetadataReference reference, string fileName, ImmutableArray<byte> image)
        {
            Contract.ThrowIfNull(fileName);
            _inMemoryImagesForTesting.Add(reference, (fileName, image));
        }

        public static bool ContainsInMemoryImage(MetadataReference reference)
        {
            return _inMemoryImagesForTesting.ContainsKey(reference);
        }

        public static void ClearInMemoryImages()
        {
            _inMemoryImagesForTesting.Clear();
        }
    }
}
