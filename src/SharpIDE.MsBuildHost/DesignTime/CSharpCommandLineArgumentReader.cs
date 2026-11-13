using Microsoft.Build.Execution;

namespace SharpIDE.MsBuildHost.DesignTime;

// Adapted from Roslyn's BuildHost CommandLineArgumentReader and CSharpCommandLineArgumentReader (C# only)
// https://github.com/dotnet/roslyn/tree/main/src/Workspaces/MSBuild/BuildHost/MSBuild

/// Recovers csc command line args from a project's properties, for when the design-time build didn't produce any (i.e. it failed).
/// References and analyzers aren't included - the ProjectFileInfo carries those separately in that case.
internal sealed class CSharpCommandLineArgumentReader
{
	private static readonly Dictionary<string, string> DebugTypeValues = new(StringComparer.OrdinalIgnoreCase)
	{
		["none"] = "none",
		["pdbonly"] = "pdbonly",
		["full"] = "full",
		["portable"] = "portable",
		["embedded"] = "embedded"
	};

	private readonly ProjectInstance _project;
	private readonly List<string> _builder = [];

	private readonly string _projectDirectory;

	private CSharpCommandLineArgumentReader(ProjectInstance project, string projectDirectory)
	{
		_project = project;
		_projectDirectory = projectDirectory;
	}

	/// projectDirectory, rather than the instance's FullPath - a project's state after building on an out of proc node doesn't have it
	public static string[] Read(ProjectInstance project, string projectDirectory)
	{
		var reader = new CSharpCommandLineArgumentReader(project, projectDirectory);
		reader.ReadCore();
		return [.. reader._builder];
	}

	private void ReadCore()
	{
		foreach (var additionalFile in _project.GetAdditionalFiles()) Add("additionalfile", ProjectInstanceExtensions.ResolvePath(additionalFile.ItemSpec, _projectDirectory));

		var codePage = _project.ReadCodePage();
		if (codePage != 0) Add("codepage", codePage.ToString());

		if (_project.ReadPropertyBool("DebugSymbols") && _project.ReadPropertyString("DebugType") is { } debugType && DebugTypeValues.TryGetValue(debugType, out var debugTypeValue))
		{
			Add("debug", debugTypeValue);
		}

		var delaySign = _project.ReadPropertyString("DelaySign");
		if (string.IsNullOrWhiteSpace(delaySign) is false) Add(ProjectInstanceExtensions.ToBool(delaySign) ? "delaysign+" : "delaysign-");

		var errorReport = _project.ReadPropertyString("ErrorReport");
		if (string.IsNullOrWhiteSpace(errorReport) is false) Add("errorreport", errorReport.ToLowerInvariant());

		// As the compiler's own MSBuild task splits Features
		foreach (var feature in (_project.ReadPropertyString("Features") ?? "").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)) Add("features", feature);

		// For an alias clause (`aliasname = namespace`), quote that clause only, rather than the entire imports
		AddIfNotNullOrWhiteSpace("imports", string.Join(",", _project.GetItems("Import").Select(item => item.EvaluatedInclude.Trim()).Select(i => i.Contains(' ') ? $"\"{i}\"" : i)), addQuoteIfValueContainsWhitespace: false);

		var platform = _project.ReadPropertyString("PlatformTarget");
		if (_project.ReadPropertyBool("Prefer32Bit") && (string.IsNullOrWhiteSpace(platform) || string.Equals("anycpu", platform, StringComparison.OrdinalIgnoreCase)))
		{
			platform = "anycpu32bitpreferred";
		}
		AddIfNotNullOrWhiteSpace("platform", platform);

		if (_project.ReadPropertyBool("SignAssembly"))
		{
			AddIfNotNullOrWhiteSpace("keyFile", _project.ReadPropertyString("KeyOriginatorFile"));
			AddIfNotNullOrWhiteSpace("keycontainer", _project.ReadPropertyString("KeyContainerName"));
		}

		AddIfNotNullOrWhiteSpace("appconfig", _project.ReadPropertyString("AppConfigForCompiler"));
		AddIfNotNullOrWhiteSpace("baseaddress", _project.ReadPropertyString("BaseAddress"));
		if (_project.ReadPropertyBool("CheckForOverflowUnderflow")) Add("checked");
		AddIfNotNullOrWhiteSpace("define", _project.ReadPropertyString("DefineConstants"));
		AddIfNotNullOrWhiteSpace("filealign", _project.ReadPropertyString("FileAlignment"));
		AddIfNotNullOrWhiteSpace("doc", _project.ReadItemsAsString("DocFileItem"));
		if (_project.ReadPropertyBool("GenerateFullPaths")) Add("fullpaths");
		if (_project.ReadPropertyBool("HighEntropyVA")) Add("highentropyva");
		AddIfNotNullOrWhiteSpace("langversion", _project.ReadPropertyString("LangVersion"));
		AddIfNotNullOrWhiteSpace("main", _project.ReadPropertyString("StartupObject"));
		AddIfNotNullOrWhiteSpace("moduleassemblyname", _project.ReadPropertyString("ModuleAssemblyName"));
		if (_project.ReadPropertyBool("NoCompilerStandardLib")) Add("nostdlib");
		AddIfNotNullOrWhiteSpace("nowarn", _project.ReadPropertyString("NoWarn"));
		if (_project.ReadPropertyBool("Optimize")) Add("optimize");
		AddIfNotNullOrWhiteSpace("out", _project.ReadItemsAsString("IntermediateAssembly"));
		AddIfNotNullOrWhiteSpace("pdb", _project.ReadPropertyString("PdbFile"));
		AddIfNotNullOrWhiteSpace("ruleset", _project.ReadPropertyString("ResolvedCodeAnalysisRuleSet"));
		AddIfNotNullOrWhiteSpace("subsystemversion", _project.ReadPropertyString("SubsystemVersion"));
		AddIfNotNullOrWhiteSpace("target", _project.ReadPropertyString("OutputType"));
		if (_project.ReadPropertyBool("AllowUnsafeBlocks")) Add("unsafe");
		Add("warn", _project.ReadPropertyInt("WarningLevel").ToString());
		if (_project.ReadPropertyBool("TreatWarningsAsErrors")) Add("warnaserror");
		AddIfNotNullOrWhiteSpace("warnaserror+", _project.ReadPropertyString("WarningsAsErrors"));
		AddIfNotNullOrWhiteSpace("warnaserror-", _project.ReadPropertyString("WarningsNotAsErrors"));
	}

	private void Add(string name) => _builder.Add($"/{name}");

	private void Add(string name, string? value, bool addQuoteIfValueContainsWhitespace = true)
	{
		_builder.Add(string.IsNullOrEmpty(value) || (addQuoteIfValueContainsWhitespace && value.Any(char.IsWhiteSpace))
			? $"/{name}:\"{value}\""
			: $"/{name}:{value}");
	}

	private void AddIfNotNullOrWhiteSpace(string name, string? value, bool addQuoteIfValueContainsWhitespace = true)
	{
		if (string.IsNullOrWhiteSpace(value) is false) Add(name, value, addQuoteIfValueContainsWhitespace);
	}
}
