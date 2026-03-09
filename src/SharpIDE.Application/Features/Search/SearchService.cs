using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Search;

public class SearchService(ILogger<SearchService> logger)
{
	private readonly ILogger<SearchService> _logger = logger;

	public async IAsyncEnumerable<FindInFilesSearchResult> FindInFiles(
		SharpIdeSolutionModel solutionModel,
		string searchTerm,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (searchTerm.Length < 4) // TODO: halt search once 100 results are found, and remove this restriction
		{
			yield break;
		}

		var timer = Stopwatch.StartNew();
		var files = solutionModel.AllFiles.Values;
		var resultChannel = Channel.CreateUnbounded<FindInFilesSearchResult>(
			new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = false
			});

		var searchTask = Task.Run(async () =>
		{
			try
			{
				var parallelOptions = new ParallelOptions
				{
					CancellationToken = cancellationToken,
					MaxDegreeOfParallelism = Environment.ProcessorCount
				};

				await Parallel.ForEachAsync(
					files,
					parallelOptions,
					(file, ct) => FindInFile(file, searchTerm, resultChannel.Writer, ct));
			}
			finally
			{
				resultChannel.Writer.Complete();
			}
		},
		cancellationToken);

		var resultCount = 0;
		await foreach (var result in resultChannel.Reader.ReadAllAsync(cancellationToken))
		{
			resultCount++;
			yield return result;
		}

		await searchTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

		timer.Stop();
		_logger.LogInformation(
			"Search completed in {ElapsedMilliseconds}ms. Found {ResultCount} results. {Cancelled}",
			timer.ElapsedMilliseconds,
			resultCount,
			cancellationToken.IsCancellationRequested ? "(Cancelled)" : "");
	}

	public async Task<List<FindFilesSearchResult>> FindFiles(SharpIdeSolutionModel solutionModel, string searchTerm, CancellationToken cancellationToken)
	{
		if (searchTerm.Length < 2) // TODO: halt search once 100 results are found, and remove this restriction
		{
			return [];
		}

		var timer = Stopwatch.StartNew();
		var files = solutionModel.AllFiles.Values.ToList();
		ConcurrentBag<FindFilesSearchResult> results = [];
		await Parallel.ForEachAsync(files, cancellationToken, async (file, ct) =>
			{
				if (cancellationToken.IsCancellationRequested) return;
				if (file.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
				{
					results.Add(new FindFilesSearchResult
					{
						File = file
					});
				}
			}
		).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		timer.Stop();
		_logger.LogInformation("File search completed in {ElapsedMilliseconds}ms. Found {ResultCount} results. {Cancelled}", timer.ElapsedMilliseconds, results.Count, cancellationToken.IsCancellationRequested ? "(Cancelled)" : "");
		return results.ToList();
	}

	private static async ValueTask FindInFile(SharpIdeFile file, string searchTerm, ChannelWriter<FindInFilesSearchResult> resultWriter, CancellationToken ct)
	{
		if (ct.IsCancellationRequested) return;

		await foreach (var (index, line) in File.ReadLinesAsync(file.Path, ct).Index().WithCancellation(ct))
		{
			if (ct.IsCancellationRequested) return;
			if (line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) is false) continue;

			var result = new FindInFilesSearchResult
			{
				File = file,
				Line = index + 1,
				StartColumn = line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) + 1,
				LineText = line.Trim()
			};

			await resultWriter.WriteAsync(result, ct).ConfigureAwait(false);
		}
	}
}
