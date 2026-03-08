using System.Collections.Immutable;
using Godot;
using Microsoft.CodeAnalysis.Text;
using SharpIDE.Application.Features.Analysis;
using SharpIDE.Godot.Features.SymbolLookup;

namespace SharpIDE.Godot.Features.CodeEditor;

public partial class SharpIdeCodeEdit
{
    private readonly PackedScene _symbolUsagePopupScene = ResourceLoader.Load<PackedScene>("uid://dq7ss2ha5rk44");

    private void OnSymbolLookup(string symbolString, long line, long column)
    {
        GD.Print($"Symbol lookup requested: {symbolString} at line {line}, column {column}");
        var globalMousePosition = GetGlobalMousePosition(); // don't breakpoint before this, else your mouse position will be wrong
        var clickedCharRect = GetRectAtLineColumn((int)line, (int)column);
        var globalPosition = GetGlobalPosition();
        var startSymbolCharGlobalPos = clickedCharRect.Position + globalPosition;

        _ = Task.GodotRun(async () =>
        {
            var (symbol, linePositionSpan, semanticInfo) = await _roslynAnalysis.LookupSymbolSemanticInfo(_currentFile, new LinePosition((int)line, (int)column));
            if (symbol is null) return;

            //var locations = symbol.Locations;

            if (semanticInfo is null) return;
            if (semanticInfo.Value.DeclaredSymbol is not null)
            {
                GD.Print($"Symbol is declared here: {symbolString}");
                // TODO: Lookup references instead
                var references = await _roslynAnalysis.FindAllSymbolReferences(semanticInfo.Value.DeclaredSymbol);
                // Filter out primary constructor references, as they are not useful to navigate to - we are already at the symbol declaration
                // This may also filter out other useful references, so may need to revisit later
                references = references.Where(s => s.LocationsArray.Length is not 0).ToImmutableArray();
                if (references.Length is 1)
                {
                    var reference = references[0];
                    var locations = reference.LocationsArray;
                    if (locations.Length is 1)
                    {
                        // Lets jump to the definition
                        var referenceLocation = locations[0];

                        var referenceLineSpan = referenceLocation.Location.GetMappedLineSpan();
                        var sharpIdeFile = Solution!.AllFiles.GetValueOrDefault(referenceLineSpan.Path);
                        if (sharpIdeFile is null)
                        {
                            GD.Print($"Reference file not found in solution: {referenceLineSpan.Path}");
                            return;
                        }

                        await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(sharpIdeFile, new SharpIdeFileLinePosition(referenceLineSpan.Span.Start.Line, referenceLineSpan.Span.Start.Character));
                    }
                    else
                    {
                        // Show popup to select which reference to go to
                        var symbolLookupPopup = _symbolUsagePopupScene.Instantiate<SymbolLookupPopup>();
                        var ideReferenceLocationResults = await _roslynAnalysis.GetIdeReferenceLocationResults(locations);
                        symbolLookupPopup.IdeReferenceLocationResults = ideReferenceLocationResults;
                        symbolLookupPopup.Symbol = semanticInfo.Value.DeclaredSymbol;
                        symbolLookupPopup.Size = new Vector2I(1, 1); // Set tiny size so it autosizes up based on child content
                        await this.InvokeAsync(() =>
                        {
                            AddChild(symbolLookupPopup);
                            symbolLookupPopup.Position = new Vector2I((int)globalMousePosition.X - 5, (int)startSymbolCharGlobalPos.Y);
                            symbolLookupPopup.Popup();
                            var currentMousePos = GetGlobalMousePosition();
                            Input.WarpMouse(currentMousePos with {X = currentMousePos.X + 1}); // it seems that until the mouse moves, behind the popup can still receive mouse events, which causes symbol the hover symbol popup to appear.
                        });
                    }
                }
            }
            else if (semanticInfo.Value.ReferencedSymbols.Length is not 0)
            {
                var referencedSymbol = semanticInfo.Value.ReferencedSymbols.Single(); // Handle more than one when I run into it
                var locations = referencedSymbol.Locations;
                if (locations.Length is 1)
                {
                    // Lets jump to the definition
                    var definitionLocation = locations[0];
                    if (definitionLocation.IsInSource)
                    {
                        var definitionLineSpan = definitionLocation.GetMappedLineSpan();
                        var sharpIdeFile = Solution!.AllFiles.GetValueOrDefault(definitionLineSpan.Path);
                        if (sharpIdeFile is null)
                        {
                            // This file may have been decompiled, but IsInSource=true as it is in the MetadataAsSource workspace. Lets try to find a metadata as source file
                            sharpIdeFile = await _sharpIdeMetadataAsSourceService.GetOrCreateSharpIdeFileForAlreadyDecompiledMetadataAsSourceAsync(definitionLineSpan.Path);
                            if (sharpIdeFile is null)
                            {
                                GD.PrintErr($"Definition file not found in solution or as metadata as source for symbol: {referencedSymbol.Name}, definition location: {definitionLineSpan.Path}");
                                return;
                            }
                        }
                        await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(sharpIdeFile, new SharpIdeFileLinePosition(definitionLineSpan.Span.Start.Line, definitionLineSpan.Span.Start.Character));
                    }
                    else
                    {
                        GD.Print($"Definition is not in source code, attempting to navigate to metadata as source: {referencedSymbol.Name}");
                        var result = await _sharpIdeMetadataAsSourceService.CreateSharpIdeFileForMetadataAsSourceAsync(_currentFile, referencedSymbol);
                        if (result is not null)
                        {
                            var (metadataAsSourceSharpIdeFile, location) = result.Value;
                            var definitionInMetadataSourceLineSpan = location.GetMappedLineSpan();
                            var linePosition = new SharpIdeFileLinePosition(definitionInMetadataSourceLineSpan.Span.Start.Line, definitionInMetadataSourceLineSpan.Span.Start.Character);
                            await GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelAsync(metadataAsSourceSharpIdeFile, linePosition);
                        }
                        else
                        {
                            GD.PrintErr($"Failed to create metadata as source file for symbol: {referencedSymbol.Name}");
                        }
                    }
                }
                else
                {
                    // TODO: Show a popup to select which definition location to go to
                }
            }
        });
    }
}