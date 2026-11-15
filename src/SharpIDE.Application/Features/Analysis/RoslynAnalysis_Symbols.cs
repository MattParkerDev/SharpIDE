using System.Collections.Immutable;
using Ardalis.GuardClauses;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.Remote.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.Analysis.WorkspaceServices;
using SharpIDE.Application.Features.SolutionDiscovery;


namespace SharpIDE.Application.Features.Analysis;

public partial class RoslynAnalysis
{
	public record IdeReferenceLocationResult(ReferenceLocation ReferenceLocation, SharpIdeFile? File, ISymbol? EnclosingSymbol);
	public async Task<IdeReferenceLocationResult?> GetIdeReferenceLocationResult(ReferenceLocation referenceLocation)
	{
		var semanticModel = await referenceLocation.Document.GetSemanticModelAsync();
		if (semanticModel is null) return null;
		var enclosingSymbol = ReferenceLocationExtensions.GetEnclosingMethodOrPropertyOrField(semanticModel, referenceLocation);
		var lineSpan = referenceLocation.Location.GetMappedLineSpan();
		var file = _sharpIdeSolutionModel!.AllFiles[lineSpan.Path];
		var result = new IdeReferenceLocationResult(referenceLocation, file, enclosingSymbol);
		return result;
	}

	public async Task<ImmutableArray<IdeReferenceLocationResult>> GetIdeReferenceLocationResults(ImmutableArray<ReferenceLocation> referenceLocations)
	{
		var results = new List<IdeReferenceLocationResult>();
		foreach (var referenceLocation in referenceLocations)
		{
			var result = await GetIdeReferenceLocationResult(referenceLocation);
			if (result is not null)
			{
				results.Add(result);
			}
		}
		return results.ToImmutableArray();
	}

	/// Returns the file changes that applying the rename would make. Does not apply the changes to the workspace sln
	public async Task<SolutionFileChanges> GetRenameApplyChanges(ISymbol symbol, string newName, CancellationToken cancellationToken = default)
	{
		var symbolRenameOptions = new SymbolRenameOptions
		{
			RenameOverloads = true,
			RenameInStrings = false,
			RenameInComments = false,
			RenameFile = false
		};
		var currentSolution = _workspace!.CurrentSolution;
		var newSolution = await Renamer.RenameSymbolAsync(currentSolution, symbol, symbolRenameOptions, newName, cancellationToken);
		return await GetSolutionFileChanges(currentSolution, newSolution, cancellationToken);
	}

	public async Task<string?> GetMetadataAsSourceFromDebuggingAssemblyAndType(string typeName, string assemblyPath, Guid mvid, string userCodeCallingAssemblyPath, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(GetMetadataAsSourceFromDebuggingAssemblyAndType)}");
		await _solutionLoadedTcs.Task;

		var callingProject = _workspace!.CurrentSolution.Projects.SingleOrDefault(p => p.OutputFilePath == userCodeCallingAssemblyPath);
		if (callingProject is null) return null;

		var runtimeReference = MetadataReference.CreateFromFile(assemblyPath);
		if (runtimeReference.GetMetadata() is not AssemblyMetadata runtimeMetadata || runtimeMetadata.GetMvid() != mvid) return null;

		// Read the full identity from the exact runtime assembly before adding it to the calling project's compilation.
		var identityCompilation = CSharpCompilation.Create(assemblyName: null, references: [runtimeReference], options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		if (identityCompilation.GetAssemblyOrModuleSymbol(runtimeReference) is not IAssemblySymbol runtimeAssemblyForIdentity) return null;

		var compilation = await callingProject.GetRequiredCompilationAsync(cancellationToken);
		var conflictingReferences = callingProject.MetadataReferences
			.Where(reference => compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly && assembly.Identity.Equals(runtimeAssemblyForIdentity.Identity))
			.ToImmutableArray();
		if (conflictingReferences.IsEmpty is false)
		{
			runtimeReference = runtimeReference.WithProperties(conflictingReferences[0].Properties);
			foreach (var conflictingReference in conflictingReferences)
			{
				callingProject = callingProject.RemoveMetadataReference(conflictingReference);
			}
		}

		callingProject = callingProject.AddMetadataReference(runtimeReference);
		compilation = await callingProject.GetRequiredCompilationAsync(cancellationToken);
		var runtimeAssembly = compilation.GetAssemblyOrModuleSymbol(runtimeReference) as IAssemblySymbol;
		var symbol = runtimeAssembly?.GetTypeByMetadataName(typeName);
		if (symbol is null) return null;

		var options = MetadataAsSourceOptions.Default;// with { NavigateToSourceLinkAndEmbeddedSources = false };
		var metadataAsSourceFile = await _metadataAsSourceFileService.GetGeneratedFileAsync(_workspace, callingProject, symbol, false, options, cancellationToken);
		return metadataAsSourceFile.FilePath;
	}

	public async Task<string?> WriteSourceFromMetadataAsSourceWorkspaceToDisk(string filePath, CancellationToken cancellationToken = default)
	{
		await _solutionLoadedTcs.Task;
		var metadataAsSourceWorkspace = _metadataAsSourceFileService.TryGetWorkspace();
		var documentId = metadataAsSourceWorkspace!.CurrentSolution.GetDocumentIdsWithFilePath(filePath).SingleOrDefault();
		var document = metadataAsSourceWorkspace.CurrentSolution.GetDocument(documentId);
		if (document is null) return null;
		await DecompileWholeAssemblyToProjectMetadataAsSourceFileProvider.WriteFileToDiskAsync(document, cancellationToken);
		return document.FilePath;
	}

	public async Task<(string FilePath, string PdbSourceFilePath, Location Location)?> GetMetadataAsSourceForSymbol(SharpIdeFile currentFile, ISymbol symbol, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(FindAllSymbolReferences)}");
		await _solutionLoadedTcs.Task;
		if (_metadataAsSourceFileService.IsNavigableMetadataSymbol(symbol) is false) return null;

		var documentContainingMetadataReference = await GetDocumentForSharpIdeFile(currentFile, cancellationToken);

		var options = MetadataAsSourceOptions.Default;// with { NavigateToSourceLinkAndEmbeddedSources = false };
		var metadataAsSourceFile = await _metadataAsSourceFileService.GetGeneratedFileAsync(_workspace!, documentContainingMetadataReference.Project, symbol, false, options, cancellationToken);
		var metadataAsSourceWorkspace = _metadataAsSourceFileService.TryGetWorkspace();
		var documentId = metadataAsSourceWorkspace!.CurrentSolution.GetDocumentIdsWithFilePath(metadataAsSourceFile.FilePath).SingleOrDefault();
		if (documentId is null) return null;
		var document = await metadataAsSourceWorkspace.CurrentSolution.GetRequiredDocumentAsync(documentId, cancellationToken);
		if (document.FilePath is null) return null;
		return (document.FilePath, document.Name, metadataAsSourceFile.IdentifierLocation);
	}

	public async Task<ImmutableArray<ReferencedSymbol>> FindAllSymbolReferences(ISymbol symbol, CancellationToken cancellationToken = default)
	{
		using var _ = SharpIdeOtel.Source.StartActivity($"{nameof(RoslynAnalysis)}.{nameof(FindAllSymbolReferences)}");
		await _solutionLoadedTcs.Task;

		var solution = _workspace!.CurrentSolution;
		var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
		return references.ToImmutableArray();
	}

	public async Task<(ISymbol?, LinePositionSpan?, TokenSemanticInfo?)> LookupSymbolSemanticInfo(SharpIdeFile fileModel, LinePosition linePosition)
	{
		await _solutionLoadedTcs.Task;
		var (symbol, linePositionSpan, semanticInfo) = fileModel switch
		{
			{ IsRazorFile: true } => await LookupSymbolSemanticInfoInRazor(fileModel, linePosition),
			{ IsCsharpFile: true } => await LookupSymbolSemanticInfoInCs(fileModel, linePosition),
			_ => (null, null, null)
		};
		return (symbol, linePositionSpan, semanticInfo);
	}

	public async Task<(ISymbol?, LinePositionSpan?)> LookupSymbol(SharpIdeFile fileModel, LinePosition linePosition)
	{
		await _solutionLoadedTcs.Task;
		var (symbol, linePositionSpan) = fileModel switch
		{
			{ IsRazorFile: true } => await LookupSymbolInRazor(fileModel, linePosition),
			{ IsCsharpFile: true } => await LookupSymbolInCs(fileModel, linePosition),
			_ => (null, null)
		};
		return (symbol, linePositionSpan);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan)> LookupSymbolInRazor(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var additionalDocument = GetRequiredTextDocumentForSharpIdeFile(fileModel);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(additionalDocument.Project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(additionalDocument);

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);
		var razorAbsoluteIndex = razorText.Lines.GetPosition(linePosition);
		var mapped = await MapRazorPositionToGeneratedCSharp(razorProjectSnapshot, documentSnapshot, razorAbsoluteIndex, cancellationToken);
		if (mapped is null) return (null, null);
		var (razorCSharpDocument, generatedDocument, mappedPosition) = mapped.Value;
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var semanticModelAsync = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModelAsync!, generatedDocSyntaxRoot!, mappedPosition);
		if (symbol is null || linePositionSpan is null) return (null, null);
		Guard.Against.Null(linePositionSpan, nameof(linePositionSpan));
		if (_documentMappingService!.TryMapToRazorDocumentRange(razorCSharpDocument, linePositionSpan.Value, MappingBehavior.Strict, out var mappedRazorLinePositionSpan) is false)
		{
			throw new InvalidOperationException("Failed to map C# line position span back to Razor.");
		}
		return (symbol, mappedRazorLinePositionSpan);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan)> LookupSymbolInCs(SharpIdeFile fileModel, LinePosition linePosition)
	{
		var document = GetRequiredCSharpDocumentForSharpIdeFile(fileModel);
		var sourceText = await document.GetTextAsync();
		var position = sourceText.GetPosition(linePosition);
		var semanticModel = await document.GetSemanticModelAsync();
		Guard.Against.Null(semanticModel, nameof(semanticModel));
		var syntaxRoot = await document.GetSyntaxRootAsync();
		return GetSymbolAtPosition(semanticModel, syntaxRoot!, position);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan, TokenSemanticInfo? semanticInfo)> LookupSymbolSemanticInfoInCs(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var document = GetRequiredCSharpDocumentForSharpIdeFile(fileModel);
		var sourceText = await document.GetTextAsync(cancellationToken);
		var position = sourceText.GetPosition(linePosition);
		var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		Guard.Against.Null(semanticModel, nameof(semanticModel));
		var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
		var semanticInfo = await SymbolFinder.GetSemanticInfoAtPositionAsync(semanticModel, position, document.Project.Solution.Services, cancellationToken).ConfigureAwait(false);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModel, syntaxRoot!, position);
		return (symbol, linePositionSpan, semanticInfo);
	}

	private async Task<(ISymbol? symbol, LinePositionSpan? linePositionSpan, TokenSemanticInfo? semanticInfo)> LookupSymbolSemanticInfoInRazor(SharpIdeFile fileModel, LinePosition linePosition, CancellationToken cancellationToken = default)
	{
		var additionalDocument = GetRequiredTextDocumentForSharpIdeFile(fileModel);

		var razorProjectSnapshot = _snapshotManager!.GetSnapshot(additionalDocument.Project);
		var documentSnapshot = razorProjectSnapshot.GetDocument(additionalDocument);

		var razorText = await additionalDocument.GetTextAsync(cancellationToken);
		var razorAbsoluteIndex = razorText.Lines.GetPosition(linePosition);
		var mapped = await MapRazorPositionToGeneratedCSharp(razorProjectSnapshot, documentSnapshot, razorAbsoluteIndex, cancellationToken);
		if (mapped is null) return (null, null, null);
		var (_, generatedDocument, mappedPosition) = mapped.Value;
		var generatedDocSyntaxRoot = await generatedDocument.GetSyntaxRootAsync(cancellationToken);

		var semanticModel = await generatedDocument.GetSemanticModelAsync(cancellationToken);
		var (symbol, linePositionSpan) = GetSymbolAtPosition(semanticModel!, generatedDocSyntaxRoot!, mappedPosition);

		var semanticInfo = await SymbolFinder.GetSemanticInfoAtPositionAsync(semanticModel!, mappedPosition, generatedDocument.Project.Solution.Services, cancellationToken).ConfigureAwait(false);
		return (symbol, linePositionSpan, semanticInfo);
	}

	private (ISymbol? symbol, LinePositionSpan? linePositionSpan) GetSymbolAtPosition(SemanticModel semanticModel, SyntaxNode root, int position)
	{
		var node = root.FindToken(position).Parent!;
		var symbol = semanticModel.GetSymbolInfo(node).Symbol ?? semanticModel.GetDeclaredSymbol(node);
		if (symbol is null)
		{
			_logger.LogInformation("No symbol found at position {Position}", position);
			return (null, null);
		}

		var span = node switch
		{
			MethodDeclarationSyntax methodDecl => methodDecl.Identifier.Span,
			ClassDeclarationSyntax classDecl => classDecl.Identifier.Span,
			StructDeclarationSyntax structDecl => structDecl.Identifier.Span,
			InterfaceDeclarationSyntax interfaceDecl => interfaceDecl.Identifier.Span,
			EnumDeclarationSyntax enumDecl => enumDecl.Identifier.Span,
			DelegateDeclarationSyntax delegateDecl => delegateDecl.Identifier.Span,
			ConstructorDeclarationSyntax constructorDecl => constructorDecl.Identifier.Span,
			DestructorDeclarationSyntax destructorDecl => destructorDecl.Identifier.Span,
			PropertyDeclarationSyntax propDecl => propDecl.Identifier.Span,
			EventDeclarationSyntax eventDecl => eventDecl.Identifier.Span,
			VariableDeclaratorSyntax variableDecl => variableDecl.Identifier.Span,
			AccessorDeclarationSyntax accessorDecl => accessorDecl.Keyword.Span,
			IndexerDeclarationSyntax indexerDecl => indexerDecl.ThisKeyword.Span,

			GenericNameSyntax genericDecl => genericDecl.Identifier.Span,
			IdentifierNameSyntax identifierDecl => identifierDecl.Identifier.Span,
			_ => node.Span
		};

		var linePositionSpan = root.SyntaxTree.GetLineSpan(span).Span;
		_logger.LogInformation("Symbol found: {SymbolName} ({SymbolKind}) - {SymbolDisplayString}", symbol.Name, symbol.Kind, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
		return (symbol, linePositionSpan);
	}

	// Razor emits two C# documents - the impl document, and the declaration document (e.g. @code block members).
	// Map the razor position into whichever one contains it, and return the matching source generated document.
	private static async Task<(RazorCSharpDocument CSharpDocument, SourceGeneratedDocument GeneratedDocument, int CSharpIndex)?> MapRazorPositionToGeneratedCSharp(RemoteProjectSnapshot razorProjectSnapshot, RemoteDocumentSnapshot documentSnapshot, int razorAbsoluteIndex, CancellationToken cancellationToken)
	{
		var razorCodeDocument = await razorProjectSnapshot.GetRequiredCodeDocumentAsync(documentSnapshot, cancellationToken);
		if (DocumentMappingHelper.TryMapToCSharpDocumentLinePosition(razorCodeDocument, razorAbsoluteIndex, out _, out var csharpIndex, out var inDeclDocument) is false)
		{
			return null;
		}

		if (inDeclDocument)
		{
			var declGeneratedDocument = await razorProjectSnapshot.TryGetDeclGeneratedDocumentAsync(documentSnapshot, cancellationToken);
			if (declGeneratedDocument is null) return null;
			return (razorCodeDocument.GetCSharpDocument(true)!, declGeneratedDocument, csharpIndex);
		}

		var generatedDocument = await razorProjectSnapshot.GetRequiredGeneratedDocumentAsync(documentSnapshot, cancellationToken);
		return (razorCodeDocument.GetRequiredCSharpDocument(false), generatedDocument, csharpIndex);
	}
}
