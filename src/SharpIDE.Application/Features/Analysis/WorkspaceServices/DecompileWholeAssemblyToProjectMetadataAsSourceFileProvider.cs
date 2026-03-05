using System.Collections.Concurrent;
using System.Composition;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Debugging;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.PdbSourceDocument;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Structure;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Document = Microsoft.CodeAnalysis.Document;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace SharpIDE.Application.Features.Analysis.WorkspaceServices;

[ExportMetadataAsSourceFileProvider(ProviderName), Shared]
[ExtensionOrder(Before = PdbSourceDocumentMetadataAsSourceFileProvider.ProviderName)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class DecompileWholeAssemblyToProjectMetadataAsSourceFileProvider(IImplementationAssemblyLookupService implementationAssemblyLookupService) : IMetadataAsSourceFileProvider
{
	internal const string ProviderName = "DecompilationWholeAssembly";

	/// <summary>
	/// Maps an assembly-level key to the temporary directory used for that assembly's decompiled project.
	/// Accessed only in <see cref="GetGeneratedFileAsync"/> and <see cref="CleanupGeneratedFiles"/>, both of which
	/// are called under a lock in <see cref="MetadataAsSourceFileService"/>.  So this is safe as a plain
	/// dictionary.
	/// </summary>
	private readonly Dictionary<UniqueAssemblyKey, AssemblyProjectInfo> _assemblyKeyToProjectInfo = [];

	/// <summary>
	/// Maps each individual decompiled file path to its document ID and the parent project metadata.
	/// Accessed both in <see cref="GetGeneratedFileAsync"/> and in UI thread operations.  Those should not
	/// generally run concurrently.  However, to be safe, we make this a concurrent dictionary to be safe to that
	/// potentially happening.
	/// </summary>
	private readonly ConcurrentDictionary<string, (MetadataAsSourceGeneratedFileInfo Metadata, DocumentId DocumentId)> _generatedFilenameToInformation = new(StringComparer.OrdinalIgnoreCase);

	private readonly IImplementationAssemblyLookupService _implementationAssemblyLookupService = implementationAssemblyLookupService;

	public async Task<MetadataAsSourceFile?> GetGeneratedFileAsync(
		MetadataAsSourceWorkspace metadataWorkspace,
		Workspace sourceWorkspace,
		Project sourceProject,
		ISymbol symbol,
		bool signaturesOnly,
		MetadataAsSourceOptions options,
		string tempPath,
		TelemetryMessage? telemetryMessage,
		CancellationToken cancellationToken)
	{
		// Use the current fallback analyzer config options from the source workspace.
		metadataWorkspace.OnSolutionFallbackAnalyzerOptionsChanged(sourceWorkspace.CurrentSolution.FallbackAnalyzerOptions);

		var topLevelNamedType = MetadataAsSourceHelpers.GetTopLevelContainingNamedType(symbol);
		var symbolId = SymbolKey.Create(symbol, cancellationToken);
		var compilation = await sourceProject.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);

		// If we've been asked for signatures only, then we never want to use the decompiler
		var useDecompiler = !signaturesOnly && options.NavigateToDecompiledSources;

		var refInfo = GetReferenceInfo(compilation, symbol);
		if (refInfo.metadataReference is null) return null;

		// If it's a reference assembly we won't get real code anyway, so better to
		// not use the decompiler, as the stubs will at least be in the right language
		// (decompiler only produces C#)
		if (useDecompiler)
		{
			useDecompiler = !refInfo.isReferenceAssembly;
		}

		var assemblyKey = await GetUniqueAssemblyKeyAsync(refInfo.metadataReference);

		DocumentId generatedDocumentId;
		Location navigateLocation;

		if (_assemblyKeyToProjectInfo.TryGetValue(assemblyKey, out var existingProjectInfo))
		{
			// The assembly has already been decompiled. Find the document for the requested type.
			var primaryFilePath = GetPrimaryFilePath(existingProjectInfo.TempDirectory, topLevelNamedType, assemblyKey.Mvid, Path.GetFileNameWithoutExtension(assemblyKey.FilePath));

			if (_generatedFilenameToInformation.TryGetValue(primaryFilePath, out var existingDocInfo))
			{
				generatedDocumentId = existingDocInfo.DocumentId;
			}
			else
			{
				throw new InvalidOperationException($"The assembly '{topLevelNamedType.ContainingAssembly.Name}' has already been decompiled, but we couldn't find a document for the requested type '{topLevelNamedType.Name}' at the expected path '{primaryFilePath}'.");
			}

			var document = await metadataWorkspace.CurrentSolution.GetRequiredDocumentAsync(generatedDocumentId, cancellationToken);
			await WriteFileToDiskAsync(document, cancellationToken).ConfigureAwait(false);
			navigateLocation = await MetadataAsSourceHelpers.GetLocationInGeneratedSourceAsync(symbolId, document, cancellationToken).ConfigureAwait(false);
		}
		else
		{
			// We don't have this assembly in the workspace yet. Decompile the whole assembly.
			var fileInfo = new MetadataAsSourceGeneratedFileInfo(tempPath, sourceWorkspace, sourceProject, topLevelNamedType, signaturesOnly: !useDecompiler);
			var tempDirectory = Path.GetDirectoryName(fileInfo.TemporaryFilePath)!;

			Dictionary<string, string>? decompiledFiles = null;
			if (useDecompiler)
			{
				try
				{
					decompiledFiles = await GetSourceFilesFromSymbolCachePdb(refInfo.metadataReference, refInfo.assemblyLocation, assemblyKey, cancellationToken);
					if (decompiledFiles is null)
					{
						decompiledFiles = await DecompileAssemblyToMemoryAsync(compilation, refInfo.metadataReference, refInfo.assemblyLocation, assemblyKey, cancellationToken);
					}

					telemetryMessage?.SetDecompiled(decompiledFiles is not null);

					if (decompiledFiles is null)
						useDecompiler = false;
				}
				catch (Exception e) when (FatalError.ReportAndCatchUnlessCanceled(e, cancellationToken, ErrorSeverity.General))
				{
					Console.WriteLine($"Failed to decompile assembly '{symbol.ContainingAssembly.Name}': {e}");
					useDecompiler = false;
					decompiledFiles = null;
				}
			}

			// Create the project and all documents.
			var (temporaryProjectInfo, primaryDocumentId, allDocumentPaths) = GenerateProjectAndDocumentInfo(
				fileInfo, metadataWorkspace.CurrentSolution.Services, sourceProject, topLevelNamedType, refInfo.metadataReference, decompiledFiles, tempDirectory);

			var temporarySolution = metadataWorkspace.CurrentSolution.AddProject(temporaryProjectInfo);

			// Find the primary document for the requested type to navigate to.
			var primaryDoc = await temporarySolution.GetRequiredDocumentAsync(primaryDocumentId, cancellationToken);
			await WriteFileToDiskAsync(primaryDoc, cancellationToken).ConfigureAwait(false);
			navigateLocation = await MetadataAsSourceHelpers.GetLocationInGeneratedSourceAsync(symbolId, primaryDoc, cancellationToken).ConfigureAwait(false);

			// Register in workspace — must not be cancelled to avoid leaving workspace in bad state.
			cancellationToken = default;

			var projectInfo = new AssemblyProjectInfo(temporaryProjectInfo.Id!, tempDirectory);
			_assemblyKeyToProjectInfo[assemblyKey] = projectInfo;

			MutateWorkspace(fileInfo, temporaryProjectInfo, allDocumentPaths, metadataWorkspace);
			generatedDocumentId = primaryDocumentId;
		}

		var documentName = string.Format(
			"{0} [{1}]",
			topLevelNamedType.Name,
			useDecompiler ? FeaturesResources.Decompiled : FeaturesResources.from_metadata);

		var documentTooltip = topLevelNamedType.ToDisplayString(new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces));

		// Return the file path of the primary document for the requested type.
		var returnFilePath = _generatedFilenameToInformation
			.FirstOrDefault(kv => kv.Value.DocumentId == generatedDocumentId).Key;

		if (returnFilePath is null)
			return null;

		return new MetadataAsSourceFile(returnFilePath, navigateLocation, documentName, documentTooltip);
	}

	private static async Task<Dictionary<string, string>?> GetSourceFilesFromSymbolCachePdb(
		MetadataReference? metadataReference,
		string? assemblyLocation,
		UniqueAssemblyKey assemblyKey,
		CancellationToken cancellationToken)
	{
		if (metadataReference is null || assemblyLocation is null) return null;

		// TBD
		var symbolCacheFolderPath = SharpIdeDecompilationConstants.SymbolCachePath;

		var assemblyName = Path.GetFileNameWithoutExtension(assemblyLocation);
		var mvid = assemblyKey.Mvid;
		var pdbPath = Path.Combine(symbolCacheFolderPath, assemblyName, mvid.ToString(), $"{assemblyName}.decompiled.pdb");
		if (!File.Exists(pdbPath)) return null;
		var sourceFiles = new Dictionary<string, string>();
		try
		{
			var pdbStream = File.OpenRead(pdbPath);
			var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
			var reader = provider.GetMetadataReader();

			foreach (var documentHandle in reader.Documents)
			{
				var document = reader.GetDocument(documentHandle);
				var documentName = reader.GetString(document.Name);

				var nullableEmbeddedSourceHandle = reader.GetCustomDebugInformation(documentHandle)
					.Select(h => reader.GetCustomDebugInformation(h))
					.Where(cdi => reader.GetGuid(cdi.Kind) == PortableCustomDebugInfoKinds.EmbeddedSource)
					.Cast<CustomDebugInformation?>()
					.FirstOrDefault();

				if (nullableEmbeddedSourceHandle is not {} embeddedSourceHandle || embeddedSourceHandle.Kind.IsNil)
				{
					continue;
				}

				var blobReader = reader.GetBlobReader(embeddedSourceHandle.Value);

				// First 4 bytes: if 0, the rest is raw UTF-8.
				// If > 0, it's the uncompressed length and the rest is deflate-compressed.
				var uncompressedSize = blobReader.ReadInt32();
				var sourceBytes = blobReader.ReadBytes(blobReader.RemainingBytes);

				string sourceText;
				if (uncompressedSize == 0)
				{
					// Raw, uncompressed UTF-8
					sourceText = Encoding.UTF8.GetString(sourceBytes);
				}
				else
				{
					// Deflate-compressed
					using var compressedStream = new MemoryStream(sourceBytes);
					await using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
					using var resultStream = new MemoryStream(uncompressedSize);
					await deflateStream.CopyToAsync(resultStream, cancellationToken);
					sourceText = Encoding.UTF8.GetString(resultStream.ToArray());
				}

				sourceFiles[documentName] = sourceText;
			}
		}
		catch (Exception e) when (FatalError.ReportAndCatchUnlessCanceled(e, cancellationToken, ErrorSeverity.General))
		{
			Console.WriteLine($"Failed to read source files from PDB '{pdbPath}': {e}");
			return null;
		}
		if (sourceFiles.Count is 0) return null;
		return sourceFiles;
	}

	/// <summary>
	/// Decompiles all types in the assembly to a dictionary of relative path -> source code,
	/// and writes a portable PDB with embedded sources to the symbol cache.
	/// </summary>
	private static Task<Dictionary<string, string>?> DecompileAssemblyToMemoryAsync(
		Compilation compilation,
		MetadataReference? metadataReference,
		string? assemblyLocation,
		UniqueAssemblyKey assemblyKey,
		CancellationToken cancellationToken)
	{
		return Task.Run(Dictionary<string, string>? () =>
		{
			var logger = new StringBuilder();
			var resolver = new AssemblyResolver2(compilation, logger);

			PEFile? file = null;
			if (metadataReference is not null)
				file = resolver.TryResolve(metadataReference, PEStreamOptions.PrefetchEntireImage);

			if (file is null && assemblyLocation is not null)
				file = new PEFile(assemblyLocation, PEStreamOptions.PrefetchEntireImage);

			if (file is null)
				return null;

			using (file)
			{
				var settings = new DecompilerSettings();
				var ts = new DecompilerTypeSystem(file, resolver, settings);
				// Determine PDB cache path.
				var assemblyName = assemblyLocation is not null ? Path.GetFileNameWithoutExtension(assemblyLocation) : file.Name;
				var pdbDir = Path.Combine(SharpIdeDecompilationConstants.SymbolCachePath, assemblyName, assemblyKey.Mvid.ToString());
				var pdbPath = Path.Combine(pdbDir, $"{assemblyName}.decompiled.pdb");
				Directory.CreateDirectory(pdbDir);

				using var pdbStream = new FileStream(pdbPath, FileMode.Create, FileAccess.Write, FileShare.None);
				var sourceFiles = PortablePdbWriter2.WritePdb(file, ts, settings, pdbStream, noLogo: true);

				return sourceFiles.Count > 0 ? sourceFiles : null;
			}
		}, cancellationToken);
	}

	private void MutateWorkspace(
		MetadataAsSourceGeneratedFileInfo fileInfo,
		ProjectInfo temporaryProjectInfo,
		IReadOnlyDictionary<string, DocumentId> allDocumentPaths,
		Workspace metadataWorkspace)
	{
		// Update all document loaders to point to the actual files on disk, then add the project.
		var updatedDocuments = temporaryProjectInfo.Documents.Select(d =>
		{
			var filePath = d.FilePath;
			if (filePath is not null && File.Exists(filePath))
			{
				var loader = new WorkspaceFileTextLoader(metadataWorkspace.CurrentSolution.Services, filePath, MetadataAsSourceGeneratedFileInfo.Encoding);
				return d.WithTextLoader(loader);
			}
			return d;
		});

		temporaryProjectInfo = temporaryProjectInfo.WithDocuments(updatedDocuments);
		metadataWorkspace.OnProjectAdded(temporaryProjectInfo);

		// Register all document paths in the lookup dictionary.
		foreach (var (filePath, documentId) in allDocumentPaths)
		{
			_generatedFilenameToInformation.TryAdd(filePath, (fileInfo, documentId));
		}
	}

	public static async Task WriteFileToDiskAsync(Document document, CancellationToken cancellationToken)
	{
		var filePath = document.FilePath;
		if (filePath is null || File.Exists(filePath)) return;
		//var textLoader = document.TextLoader;
		//if (textLoader is null) return;
		var textVersion = await document.GetTextVersionAsync(cancellationToken);
		var sourceText = await document.GetTextAsync(cancellationToken);
		var textAndVersion = TextAndVersion.Create(sourceText, textVersion);
		//var textAndVersion = await textLoader.LoadTextAndVersionAsync(new LoadTextOptions(MetadataAsSourceGeneratedFileInfo.ChecksumAlgorithm), cancellationToken).ConfigureAwait(false);

		var directoryToCreate = Path.GetDirectoryName(filePath)!;
		if (!Directory.Exists(directoryToCreate))
		{
			Directory.CreateDirectory(directoryToCreate);
		}

		await using (var textWriter = new StreamWriter(filePath, append: false, encoding: MetadataAsSourceGeneratedFileInfo.Encoding))
		{
			textAndVersion.Text.Write(textWriter, cancellationToken);
		}

		new FileInfo(filePath).IsReadOnly = true;
	}

	private static async Task WriteFilesToDiskAsync(
		ProjectInfo projectInfo,
		IReadOnlyDictionary<string, DocumentId> allDocumentPaths,
		CancellationToken cancellationToken)
	{
		// Create directories and write files.
		foreach (var document in projectInfo.Documents)
		{
			var filePath = document.FilePath;
			if (filePath is null || !allDocumentPaths.ContainsKey(filePath))
				continue;

			if (File.Exists(filePath))
				continue;

			var directoryToCreate = Path.GetDirectoryName(filePath)!;
			var stopwatch = SharedStopwatch.StartNew();
			var timeout = TimeSpan.FromSeconds(5);
			var firstAttempt = true;
			var skipWritingFile = false;

			while (!IOUtilities.PerformIO(() => Directory.Exists(directoryToCreate)))
			{
				if (stopwatch.Elapsed > timeout)
				{
					skipWritingFile = true;
					break;
				}

				if (firstAttempt)
					firstAttempt = false;
				else
					await Task.Delay(DelayTimeSpan.Short, cancellationToken).ConfigureAwait(false);

				IOUtilities.PerformIO(() => Directory.CreateDirectory(directoryToCreate));
			}

			if (skipWritingFile || File.Exists(filePath))
				continue;

			var textLoader = document.TextLoader;
			if (textLoader is null)
				continue;

			var textAndVersion = await textLoader.LoadTextAndVersionAsync(
				new LoadTextOptions(MetadataAsSourceGeneratedFileInfo.ChecksumAlgorithm), cancellationToken).ConfigureAwait(false);

			using (var textWriter = new StreamWriter(filePath, append: false, encoding: MetadataAsSourceGeneratedFileInfo.Encoding))
			{
				textAndVersion.Text.Write(textWriter, cancellationToken);
			}

			new FileInfo(filePath).IsReadOnly = true;
		}
	}

	private static (ProjectInfo projectInfo, DocumentId primaryDocumentId, IReadOnlyDictionary<string, DocumentId> allDocumentPaths) GenerateProjectAndDocumentInfo(
		MetadataAsSourceGeneratedFileInfo fileInfo,
		SolutionServices services,
		Project sourceProject,
		INamedTypeSymbol topLevelNamedType,
		PortableExecutableReference portableExecutableReference,
		Dictionary<string, string>? decompiledFiles,
		string tempDirectory)
	{
		var projectId = ProjectId.CreateNewId();

		var parseOptions = sourceProject.Language == fileInfo.LanguageName
			? sourceProject.ParseOptions
			: sourceProject.Solution.Services.GetLanguageServices(fileInfo.LanguageName).GetRequiredService<ISyntaxTreeFactoryService>().GetDefaultParseOptionsWithLatestLanguageVersion();

		// Read the assembly name and version from the actual resolved PE (after ref->impl and type-forward resolution),
		// not from the symbol in the source compilation which may point to a reference assembly.
		AssemblyIdentity? resolvedIdentity = null;
		if (portableExecutableReference.GetMetadata() is not AssemblyMetadata assemblyMetadata)
		{
			throw new InvalidOperationException("PE metadata reference must be assembly metadata.");
		}

		// The manifest module (first) contains the assembly definition with name and version.
		var manifestModule = assemblyMetadata.GetModules().FirstOrDefault();
		if (manifestModule is not null)
		{
			var reader = manifestModule.GetMetadataReader();
			var asmDef = reader.GetAssemblyDefinition();
			var name = reader.GetString(asmDef.Name);
			resolvedIdentity = new AssemblyIdentity(name, asmDef.Version);
		}

		resolvedIdentity ??= topLevelNamedType.ContainingAssembly.Identity;

		var assemblyNameForMetadataAsSourceProjectName = resolvedIdentity.Name;
		var mvid = assemblyMetadata.GetMvid();
		var assemblyVersion = resolvedIdentity.Version;

		var compilationOptions = services.GetRequiredLanguageService<ICompilationFactoryService>(fileInfo.LanguageName)
			.GetDefaultCompilationOptions()
			.WithOutputKind(OutputKind.DynamicallyLinkedLibrary);

		var documents = new List<DocumentInfo>();
		var allDocumentPaths = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

		// Track which document is the primary one for the requested type.
		DocumentId? primaryDocumentId = null;
		var primaryRelativePath = GetRelativePathForType(topLevelNamedType, mvid, assemblyNameForMetadataAsSourceProjectName);

		if (decompiledFiles is not null && decompiledFiles.Count > 0)
		{
			// Create a document for each decompiled file.
			foreach (var (relativePath, sourceCode) in decompiledFiles)
			{
				var absolutePath = Path.Combine(tempDirectory, relativePath);
				var docId = DocumentId.CreateNewId(projectId);
				var sourceText = SourceText.From(sourceCode, MetadataAsSourceGeneratedFileInfo.Encoding, MetadataAsSourceGeneratedFileInfo.ChecksumAlgorithm);
				var docInfo = DocumentInfo.Create(
					docId,
					relativePath, // store the path matching the source files in the PDB, which are virtual paths that do not exist on disk
					loader: TextLoader.From(sourceText.Container, VersionStamp.Default),
					filePath: absolutePath,
					isGenerated: true)
					.WithDesignTimeOnly(true);

				documents.Add(docInfo);
				allDocumentPaths[absolutePath] = docId;

				// Check if this is the primary file for the requested type.
				if (primaryDocumentId is null &&
					string.Equals(relativePath, primaryRelativePath, StringComparison.OrdinalIgnoreCase))
				{
					primaryDocumentId = docId;
				}
			}

			// If we didn't find an exact match, try a looser match by filename.
			if (primaryDocumentId is null)
			{
				var expectedFileName = WholeAssemblyDecompiler.CleanUpFileName(topLevelNamedType.Name, ".cs");
				primaryDocumentId = allDocumentPaths
					.Where(kv => string.Equals(Path.GetFileName(kv.Key), expectedFileName, StringComparison.OrdinalIgnoreCase))
					.Select(kv => kv.Value)
					.FirstOrDefault();
			}
		}

		if (primaryDocumentId is null)
		{
			throw new ArgumentNullException(nameof(primaryDocumentId), $"Decompilation did not produce a document for the primary requested type, {topLevelNamedType}");
		}

		// Add the AssemblyInfo document (version information for InternalsVisibleTo etc.)
		var assemblyInfoDocumentId = DocumentId.CreateNewId(projectId);
		var assemblyInfoFileName = "AssemblyInfo" + fileInfo.Extension;
		var assemblyInfoString = fileInfo.LanguageName == LanguageNames.CSharp
			? string.Format(@"[assembly: System.Reflection.AssemblyVersion(""{0}"")]", assemblyVersion)
			: string.Format(@"<Assembly: System.Reflection.AssemblyVersion(""{0}"")>", assemblyVersion);
		var assemblyInfoSourceText = SourceText.From(assemblyInfoString, MetadataAsSourceGeneratedFileInfo.Encoding, MetadataAsSourceGeneratedFileInfo.ChecksumAlgorithm);
		var assemblyInfoDocument = DocumentInfo.Create(
			assemblyInfoDocumentId,
			assemblyInfoFileName,
			loader: TextLoader.From(assemblyInfoSourceText.Container, VersionStamp.Default),
			filePath: null,
			isGenerated: true)
			.WithDesignTimeOnly(true);
		documents.Add(assemblyInfoDocument);

		var projectInfo = ProjectInfo.Create(
			new ProjectInfo.ProjectAttributes(
				id: projectId,
				version: VersionStamp.Default,
				name: assemblyNameForMetadataAsSourceProjectName,
				assemblyName: assemblyNameForMetadataAsSourceProjectName,
				language: fileInfo.LanguageName,
				compilationOutputInfo: default,
				checksumAlgorithm: MetadataAsSourceGeneratedFileInfo.ChecksumAlgorithm),
			compilationOptions: compilationOptions,
			parseOptions: parseOptions,
			documents: documents,
			metadataReferences: [.. sourceProject.MetadataReferences]);

		return (projectInfo, primaryDocumentId, allDocumentPaths);
	}

	/// <summary>
	/// Computes the expected relative path for a type, matching <see cref="WholeAssemblyDecompiler"/>'s naming logic.
	/// </summary>
	private static string GetRelativePathForType(INamedTypeSymbol topLevelNamedType, Guid mvid, string assemblyName)
	{
		var fileName = WholeAssemblyDecompiler.CleanUpFileName(topLevelNamedType.Name, ".cs");
		var ns = topLevelNamedType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
		if (string.IsNullOrEmpty(ns)) return fileName;

		var dir = WholeAssemblyDecompiler.CleanUpDirectoryName(ns);
		return Path.Combine("decompiled", assemblyName, mvid.ToString(), dir, fileName);
	}

	/// <summary>
	/// Gets the absolute path for the primary file of a type within the assembly's temp directory.
	/// </summary>
	private static string GetPrimaryFilePath(string tempDirectory, INamedTypeSymbol topLevelNamedType, Guid mvid, string assemblyName)
		=> Path.Combine(tempDirectory, GetRelativePathForType(topLevelNamedType, mvid, assemblyName));

	private (PortableExecutableReference? metadataReference, string? assemblyLocation, bool isReferenceAssembly) GetReferenceInfo(Compilation compilation, ISymbol symbolToFind)
	{
		var containingAssembly = symbolToFind.ContainingAssembly;
		var metadataReference = (PortableExecutableReference?)compilation.GetMetadataReference(containingAssembly);
		var assemblyLocation = metadataReference?.FilePath;

		var isReferenceAssembly = MetadataAsSourceHelpers.IsReferenceAssembly(containingAssembly);

		if (assemblyLocation is not null && isReferenceAssembly)
		{
			if (_implementationAssemblyLookupService.TryFindImplementationAssemblyPath(assemblyLocation, out assemblyLocation))
			{
				isReferenceAssembly = false;
				assemblyLocation = _implementationAssemblyLookupService.FollowTypeForwards(symbolToFind, assemblyLocation, null);
				if (assemblyLocation is null)
				{
					throw new InvalidOperationException($"Failed to follow type forwards for symbol '{symbolToFind.Name}', assembly '{containingAssembly.Name}' at location '{assemblyLocation}'.");
				}
				metadataReference = MetadataReference.CreateFromFile(assemblyLocation);
			}
		}

		return (metadataReference, assemblyLocation, isReferenceAssembly);
	}

	public bool ShouldCollapseOnOpen(MetadataAsSourceWorkspace workspace, string filePath, BlockStructureOptions blockStructureOptions)
	{
		if (_generatedFilenameToInformation.TryGetValue(filePath, out var info))
		{
			return info.Metadata.SignaturesOnly
				? blockStructureOptions.CollapseEmptyMetadataImplementationsWhenFirstOpened
				: blockStructureOptions.CollapseMetadataImplementationsWhenFirstOpened;
		}

		return false;
	}

	private bool RemoveDocumentFromWorkspace(MetadataAsSourceWorkspace workspace, MetadataAsSourceGeneratedFileInfo fileInfo, ProjectId projectId)
	{
		// Serial access is guaranteed by the caller.
		// Remove all documents belonging to this project.
		var filesToRemove = _generatedFilenameToInformation
			.Where(kv => kv.Value.DocumentId.ProjectId == projectId)
			.Select(kv => kv.Key)
			.ToList();

		bool removed = false;
		foreach (var filePath in filesToRemove)
		{
			if (_generatedFilenameToInformation.TryRemove(filePath, out var documentIdInfo))
			{
				removed = true;
				workspace.OnDocumentClosed(documentIdInfo.DocumentId,
					new WorkspaceFileTextLoader(workspace.Services.SolutionServices, filePath, MetadataAsSourceGeneratedFileInfo.Encoding));
			}
		}

		if (removed)
		{
			workspace.OnProjectRemoved(projectId);
		}

		return removed;
	}

	public Project? MapDocument(Document document)
	{
		if (document.FilePath is not null && _generatedFilenameToInformation.TryGetValue(document.FilePath, out var documentIdInfo))
		{
			var fileInfo = documentIdInfo.Metadata;
			var solution = fileInfo.Workspace.CurrentSolution;
			var project = solution.GetProject(fileInfo.SourceProjectId);
			return project;
		}

		return null;
	}

	public void CleanupGeneratedFiles(MetadataAsSourceWorkspace workspace)
	{
		foreach (var (assemblyKey, projectInfo) in _assemblyKeyToProjectInfo.ToList())
		{
			// Find one representative fileInfo for this project.
			var representative = _generatedFilenameToInformation.Values
				.FirstOrDefault(v => v.DocumentId.ProjectId == projectInfo.ProjectId);
			if (representative.Metadata is not null)
			{
				RemoveDocumentFromWorkspace(workspace, representative.Metadata, projectInfo.ProjectId);
			}
		}

		_generatedFilenameToInformation.Clear();
		_assemblyKeyToProjectInfo.Clear();
	}

	private static async Task<UniqueAssemblyKey> GetUniqueAssemblyKeyAsync(PortableExecutableReference peMetadataReference)
	{
		if (peMetadataReference.FilePath == null) throw new InvalidOperationException("PE metadata reference must have a file path.");
		if (peMetadataReference.GetMetadata() is not AssemblyMetadata assemblyMetadata) throw new InvalidOperationException("PE metadata reference must be assembly metadata.");
		return new UniqueAssemblyKey(peMetadataReference.FilePath, assemblyMetadata.GetMvid());
	}

	/// <summary>
	/// Tracks the project created for a decompiled assembly.
	/// </summary>
	private sealed record AssemblyProjectInfo(ProjectId ProjectId, string TempDirectory);
	private sealed record UniqueAssemblyKey(string FilePath, Guid Mvid);
}
