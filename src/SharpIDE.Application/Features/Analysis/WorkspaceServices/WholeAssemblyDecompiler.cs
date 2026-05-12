using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using NuGet.Packaging;

namespace SharpIDE.Application.Features.Analysis.WorkspaceServices
{
	/// <summary>
	/// Decompiles an assembly's source files into memory.
	/// </summary>
	public class WholeAssemblyDecompiler
	{
		const int maxSegmentLength = 255;

		public DecompilerSettings Settings { get; }
		public IAssemblyResolver AssemblyResolver { get; }
		public IDebugInfoProvider? DebugInfoProvider { get; }
		public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
		public IProgress<DecompilationProgress>? ProgressIndicator { get; set; }

		public WholeAssemblyDecompiler(IAssemblyResolver assemblyResolver)
			: this(new DecompilerSettings(), assemblyResolver, debugInfoProvider: null)
		{
		}

		public WholeAssemblyDecompiler(
			DecompilerSettings settings,
			IAssemblyResolver assemblyResolver,
			IDebugInfoProvider? debugInfoProvider)
		{
			Settings = settings ?? throw new ArgumentNullException(nameof(settings));
			AssemblyResolver = assemblyResolver ?? throw new ArgumentNullException(nameof(assemblyResolver));
			DebugInfoProvider = debugInfoProvider;
		}

		protected virtual bool IncludeTypeWhenDecompilingProject(MetadataFile module, TypeDefinitionHandle type)
		{
			var metadata = module.Metadata;
			var typeDef = metadata.GetTypeDefinition(type);
			string name = metadata.GetString(typeDef.Name);
			string ns = metadata.GetString(typeDef.Namespace);
			if (name == "<Module>" || CSharpDecompiler.MemberIsHidden(module, type, Settings))
				return false;
			if (ns == "XamlGeneratedNamespace" && name == "GeneratedInternalTypeHelper")
				return false;
			if (!typeDef.IsNested && ICSharpCode.Decompiler.CSharp.Transforms.RemoveEmbeddedAttributes.attributeNames.Contains(ns + "." + name))
				return false;
			return true;
		}

		protected virtual CSharpDecompiler CreateDecompiler(DecompilerTypeSystem ts)
		{
			var decompiler = new CSharpDecompiler(ts, Settings);
			decompiler.DebugInfoProvider = DebugInfoProvider;
			decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
			decompiler.AstTransforms.Add(new RemoveCLSCompliantAttribute());
			return decompiler;
		}

		/// <summary>
		/// Decompiles all source files in the assembly into memory.
		/// </summary>
		/// <returns>A dictionary mapping relative file paths to their decompiled source code.</returns>
		public Dictionary<string, string> DecompileToMemory(MetadataFile module, CancellationToken cancellationToken = default)
		{
			var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var metadata = module.Metadata;
			var ts = new DecompilerTypeSystem(module, AssemblyResolver, Settings);

			var files = metadata.GetTopLevelTypeDefinitions()
				.Where(td => IncludeTypeWhenDecompilingProject(module, td))
				.GroupBy(td => GetRelativeFilePathForHandle(td, metadata), StringComparer.OrdinalIgnoreCase)
				.ToList();

			var progressReporter = ProgressIndicator;
			var progress = new DecompilationProgress { TotalUnits = files.Count + 1, Title = "Decompiling..." };

			var workList = new HashSet<TypeDefinitionHandle>();
			var processedTypes = new HashSet<TypeDefinitionHandle>();

			ProcessFiles(files);

			while (workList.Count > 0)
			{
				var additionalFiles = workList
					.GroupBy(td => GetRelativeFilePathForHandle(td, metadata), StringComparer.OrdinalIgnoreCase)
					.ToList();
				workList.Clear();
				ProcessFiles(additionalFiles);
				files.AddRange(additionalFiles);
				progress.TotalUnits = files.Count + 1;
			}

			// AssemblyInfo
			var assemblyInfoDecompiler = CreateDecompiler(ts);
			assemblyInfoDecompiler.CancellationToken = cancellationToken;
			assemblyInfoDecompiler.AstTransforms.Add(new RemoveCompilerGeneratedAssemblyAttributes());
			var assemblyInfoTree = assemblyInfoDecompiler.DecompileModuleAndAssemblyAttributes();
			var assemblyInfoWriter = new StringWriter();
			assemblyInfoTree.AcceptVisitor(new CSharpOutputVisitor(assemblyInfoWriter, Settings.CSharpFormattingOptions));
			results[Path.Combine("Properties", "AssemblyInfo.cs")] = assemblyInfoWriter.ToString();

			Interlocked.Increment(ref progress.UnitsCompleted);
			progressReporter?.Report(progress);

			return new Dictionary<string, string>(results);

			string GetRelativeFilePathForHandle(TypeDefinitionHandle h, System.Reflection.Metadata.MetadataReader md)
			{
				var type = md.GetTypeDefinition(h);
				string file = CleanUpFileName(md.GetString(type.Name), ".cs");
				string ns = md.GetString(type.Namespace);
				if (string.IsNullOrEmpty(ns))
					return file;
				string dir = Settings.UseNestedDirectoriesForNamespaces ? CleanUpPath(ns) : CleanUpDirectoryName(ns);
				return Path.Combine(dir, file);
			}

			void ProcessFiles(List<IGrouping<string, TypeDefinitionHandle>> fileGroups)
			{
				foreach (var s in fileGroups.AsValueEnumerable().SelectMany(f => f)) processedTypes.Add(s);
				Parallel.ForEach(
					Partitioner.Create(fileGroups, loadBalance: true),
					new ParallelOptions {
						MaxDegreeOfParallelism = this.MaxDegreeOfParallelism,
						CancellationToken = cancellationToken
					},
					file => {
						try
						{
							var decompiler = CreateDecompiler(ts);
							decompiler.CancellationToken = cancellationToken;
							var declaredTypes = file.ToArray();
							var syntaxTree = decompiler.DecompileTypes(declaredTypes);

							foreach (var node in syntaxTree.Descendants)
							{
								var td = (node.GetResolveResult() as TypeResolveResult)?.Type.GetDefinition();
								if (td?.ParentModule != ts.MainModule)
									continue;
								while (td?.DeclaringTypeDefinition != null)
									td = td.DeclaringTypeDefinition;
								if (td != null && td.MetadataToken is { IsNil: false } token && !processedTypes.Contains((TypeDefinitionHandle)token))
								{
									lock (workList)
										workList.Add((TypeDefinitionHandle)token);
								}
							}

							var writer = new StringWriter();
							syntaxTree.AcceptVisitor(new CSharpOutputVisitor(writer, Settings.CSharpFormattingOptions));
							results[file.Key] = writer.ToString();
						}
						catch (Exception innerException) when (innerException is not OperationCanceledException && innerException is not DecompilerException)
						{
							throw new DecompilerException(module, $"Error decompiling for '{file.Key}'", innerException);
						}
						progress.Status = file.Key;
						Interlocked.Increment(ref progress.UnitsCompleted);
						progressReporter?.Report(progress);
					});
			}
		}

		public static string CleanUpFileName(string text, string extension)
		{
			if (!extension.StartsWith("."))
				extension = "." + extension;
			text = text + extension;
			return CleanUpName(text, separateAtDots: false, treatAsFileName: true, treatAsPath: false);
		}

		public static string SanitizeFileName(string fileName)
		{
			return CleanUpName(fileName, separateAtDots: false, treatAsFileName: true, treatAsPath: true);
		}

		public static string CleanUpDirectoryName(string text)
		{
			return CleanUpName(text, separateAtDots: false, treatAsFileName: false, treatAsPath: false);
		}

		public static string CleanUpPath(string text)
		{
			return CleanUpName(text, separateAtDots: true, treatAsFileName: false, treatAsPath: true)
				.Replace('.', Path.DirectorySeparatorChar);
		}

		static string CleanUpName(string text, bool separateAtDots, bool treatAsFileName, bool treatAsPath)
		{
			string? extension = null;
			int currentSegmentLength = 0;
			if (treatAsFileName)
			{
				int lastDot = text.LastIndexOf('.');
				if (lastDot >= 0 && text.Length - lastDot < maxSegmentLength)
				{
					string originalText = text;
					extension = text.Substring(lastDot);
					text = text.Remove(lastDot);
					foreach (var c in extension)
					{
						if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
						{
							extension = null;
							text = originalText;
							break;
						}
					}
				}
			}
			int pos = text.IndexOf(':');
			if (pos > 0)
				text = text.Substring(0, pos);
			text = text.Trim();
			pos = text.IndexOf('`');
			if (pos > 0)
				text = text.Substring(0, pos).Trim();

			var b = new StringBuilder(text.Length + (extension?.Length ?? 0));
			bool countBytes = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
			foreach (var c in text)
			{
				if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
				{
					unsafe
					{
						currentSegmentLength += countBytes ? Encoding.UTF8.GetByteCount(&c, 1) : 1;
					}
					if (currentSegmentLength <= maxSegmentLength)
						b.Append(c);
				}
				else if (c == '.' && b.Length > 0 && b[^1] != '.')
				{
					currentSegmentLength++;
					if (separateAtDots || currentSegmentLength <= maxSegmentLength)
						b.Append('.');
					if (separateAtDots)
						currentSegmentLength = 0;
				}
				else if (treatAsPath && (c is '/' or '\\') && currentSegmentLength > 0)
				{
					b.Append(Path.DirectorySeparatorChar);
					currentSegmentLength = 0;
				}
				else
				{
					if (char.IsHighSurrogate(c))
						continue;
					currentSegmentLength++;
					if (currentSegmentLength <= maxSegmentLength)
						b.Append('-');
				}
			}
			if (b.Length == 0)
				b.Append('-');
			string name = b.ToString();
			if (extension != null)
			{
				if (name.Length + extension.Length > maxSegmentLength)
					name = name.Remove(name.Length - extension.Length);
				name += extension;
			}
			if (IsReservedFileSystemName(name))
				return name + "_";
			else if (name == ".")
				return "_";
			else
				return name;
		}

		static bool IsReservedFileSystemName(string name)
		{
			switch (name.ToUpperInvariant())
			{
				case "AUX":
				case "COM1":
				case "COM2":
				case "COM3":
				case "COM4":
				case "COM5":
				case "COM6":
				case "COM7":
				case "COM8":
				case "COM9":
				case "CON":
				case "LPT1":
				case "LPT2":
				case "LPT3":
				case "LPT4":
				case "LPT5":
				case "LPT6":
				case "LPT7":
				case "LPT8":
				case "LPT9":
				case "NUL":
				case "PRN":
					return true;
				default:
					return false;
			}
		}
	}
}
