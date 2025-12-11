using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace SharpIDE.SourceGenerators.InputMapName;

[Generator]
public class InputMapNameGenerator : IIncrementalGenerator
{
	private static readonly Regex _inputNameRegex = new(@"\b(\w+)\s*=\s*\{", RegexOptions.Compiled);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputMapNamesProvider = context.AdditionalTextsProvider
            .Where(at => Path.GetFileName(at.Path).Equals("project.godot", StringComparison.OrdinalIgnoreCase));
        context.RegisterSourceOutput(inputMapNamesProvider, GenerateInputMapNames);
    }

    private static void GenerateInputMapNames(SourceProductionContext context, AdditionalText additionalText)
    {
        var text = additionalText.GetText(context.CancellationToken);
        if (text is null)
            return;

        // Parse the input map names from the project.godot file
        var matchCollection = _inputNameRegex.Matches(text.ToString());
	    var inputMapNames = matchCollection.Cast<Match>().Select(match => match.Groups[1].Value).ToList();

        // Generate the source code
        var sourceText = InputMapNameConstants.Code.Replace(InputMapNameConstants.PropertiesPlaceholder, GenerateProperties(inputMapNames));

        // Add the source code to the compilation
        context.AddSource("InputMapNames.g.cs", sourceText);
    }

    private static string GenerateProperties(IEnumerable<string> inputMapNames)
    {
        var propertiesBuilder = new System.Text.StringBuilder();
		foreach (var name in inputMapNames)
		{
			var safeName = SanitizeIdentifier(name);
			propertiesBuilder.AppendLine($"\tpublic const string {safeName} = \"{name}\";");
		}
		return propertiesBuilder.ToString();
    }
    /// <summary>
    /// Sanitizes a string to be a valid C# identifier.
    /// Replaces invalid characters with underscores and ensures the identifier does not start with a digit.
    /// </summary>
    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        // Remove invalid characters
        var sanitized = Regex.Replace(name, "[^a-zA-Z0-9_]", "");

        // If the first character is not a letter or underscore, prefix with underscore
        if (!char.IsLetter(sanitized, 0) && sanitized[0] != '_')
            sanitized = "_" + sanitized;

        return sanitized;
    }
}
