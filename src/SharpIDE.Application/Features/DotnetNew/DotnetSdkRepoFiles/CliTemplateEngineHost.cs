// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Cli.Commands.New;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.TemplatePackage;
using Microsoft.TemplateEngine.Edge;
// https://github.com/dotnet/sdk/blob/a13e02d05ec888660283ac7a5f0a48fb39185332/src/Cli/Microsoft.TemplateEngine.Cli/CliTemplateEngineHost.cs
namespace Microsoft.TemplateEngine.Cli
{
    public class CliTemplateEngineHost : DefaultTemplateEngineHost
    {
	    public static CliTemplateEngineHost CreateHost(bool disableSdkTemplates, bool disableProjectContext, FileInfo? projectPath, FileInfo? outputPath, bool isInteractive, LogLevel logLevel, ILoggerFactory loggerFactory)
	    {
	        var builtIns = new List<(Type InterfaceType, IIdentifiedComponent Instance)>();
	        builtIns.AddRange(Microsoft.TemplateEngine.Orchestrator.RunnableProjects.Components.AllComponents);
	        builtIns.AddRange(Microsoft.TemplateEngine.Edge.Components.AllComponents);
	        builtIns.AddRange(Microsoft.TemplateSearch.Common.Components.AllComponents);

	        if (!disableSdkTemplates)
	        {
	            builtIns.Add((typeof(ITemplatePackageProviderFactory), new BuiltInTemplatePackageProviderFactory()));
	            //builtIns.Add((typeof(ITemplatePackageProviderFactory), new OptionalWorkloadProviderFactory()));
	        }
	        // if (!disableProjectContext)
	        // {
	        //     builtIns.Add((typeof(IBindSymbolSource), new ProjectContextSymbolSource()));
	        //     builtIns.Add((typeof(ITemplateConstraintFactory), new ProjectCapabilityConstraintFactory()));
	        //     builtIns.Add((typeof(MSBuildEvaluator), new MSBuildEvaluator(outputDirectory: outputPath?.FullName, projectPath: projectPath?.FullName)));
	        // }

	        // builtIns.Add((typeof(IWorkloadsInfoProvider), new WorkloadsInfoProvider(
	        //         new Lazy<IWorkloadsRepositoryEnumerator>(() => new WorkloadInfoHelper(isInteractive))))
	        // );
	        //builtIns.Add((typeof(ISdkInfoProvider), new SdkInfoProvider()));

	        string preferredLang = "C#";

	        var preferences = new Dictionary<string, string>
	        {
	            { "prefs:language", preferredLang },
	            //{ "dotnet-cli-version", Product.Version },
	            //{ "RuntimeFrameworkVersion", new Muxer().SharedFxVersion },
	            //{ "NetStandardImplicitPackageVersion", new FrameworkDependencyFile().GetNetStandardLibraryVersion() ?? "" },
	        };
	        return new CliTemplateEngineHost(
	            "SharpIDE",
	            "1.0.0",
	            preferences,
	            builtIns,
	            outputPath: outputPath?.FullName,
	            logLevel: logLevel,
	            loggerFactory: loggerFactory);
	    }
        public CliTemplateEngineHost(
            string hostIdentifier,
            string version,
            Dictionary<string, string> preferences,
            IReadOnlyList<(Type InterfaceType, IIdentifiedComponent Instance)> builtIns,
            IReadOnlyList<string>? fallbackHostNames = null,
            string? outputPath = null,
            LogLevel logLevel = LogLevel.Information,
            ILoggerFactory? loggerFactory = null)
            : base(
                  hostIdentifier,
                  version,
                  preferences,
                  builtIns,
                  fallbackHostNames,
                  loggerFactory: loggerFactory)
        {
            string workingPath = FileSystem.GetCurrentDirectory();
            IsCustomOutputPath = outputPath != null;
            OutputPath = outputPath != null ? Path.Combine(workingPath, outputPath) : workingPath;
        }

        public string OutputPath { get; }

        public bool IsCustomOutputPath { get; }

        private bool GlobalJsonFileExistsInPath
        {
            get
            {
                const string fileName = "global.json";
                string? workingPath = OutputPath;
                bool found;
                do
                {
                    string checkPath = Path.Combine(workingPath, fileName);
                    found = FileSystem.FileExists(checkPath);
                    if (!found)
                    {
                        workingPath = Path.GetDirectoryName(workingPath.TrimEnd('/', '\\'));

                        if (string.IsNullOrWhiteSpace(workingPath) || !FileSystem.DirectoryExists(workingPath))
                        {
                            workingPath = null;
                        }
                    }
                }
                while (!found && (workingPath != null));

                return found;
            }
        }

        public override bool TryGetHostParamDefault(string paramName, out string? value)
        {
            switch (paramName)
            {
                case "GlobalJsonExists":
                    value = GlobalJsonFileExistsInPath.ToString();
                    return true;
                default:
                    return base.TryGetHostParamDefault(paramName, out value);
            }
        }
    }
}
