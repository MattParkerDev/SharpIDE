using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Application.Features.Search;

public enum SearchResult { Ok, InvalidSearch }
public class SearchService(ILogger<SearchService> logger)
{
	private readonly ILogger<SearchService> _logger = logger;

	public (IAsyncEnumerable<FindInFilesSearchResult>, SearchResult) FindInFiles(SharpIdeSolutionModel solutionModel, string searchTerm, CancellationToken cancellationToken)
	{
		if (searchTerm.Length < 4) // TODO: halt search once 100 results are found, and remove this restriction
		{
			return (AsyncEnumerable.Empty<FindInFilesSearchResult>(), SearchResult.InvalidSearch);
		}

		return (FindInFilesInternal(solutionModel, searchTerm, cancellationToken), SearchResult.Ok);
	}

	private async IAsyncEnumerable<FindInFilesSearchResult> FindInFilesInternal(SharpIdeSolutionModel solutionModel, string searchTerm, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (searchTerm.Length < 4) // TODO: halt search once 100 results are found, and remove this restriction
		{
			throw new UnreachableException();
		}

		var timer = Stopwatch.StartNew();
		var files = solutionModel.AllFiles.Values.ToList();
		var resultChannel = Channel.CreateUnbounded<FindInFilesSearchResult>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

		var searchTask = Task.Run(async () =>
		{
			try
			{
				await Parallel.ForEachAsync(files, cancellationToken, (file, ct) => FindInFile(file, searchTerm, resultChannel.Writer, ct)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			}
			finally
			{
				resultChannel.Writer.Complete();
			}
		},
		cancellationToken);

		var resultCount = 0;
		await foreach (var result in resultChannel.Reader.ReadAllAsync(CancellationToken.None)) // Don't pass the ct here, as we cannot configure SuppressThrowing on an IAsyncEnumerable, and would rather not have the overhead and logging of an exception. The channel will be completed when cancellation is requested, so the loop will exit in a timely manner.
		{
			if (cancellationToken.IsCancellationRequested) yield break;
			resultCount++;
			yield return result;
		}

		await searchTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

		timer.Stop();
		_logger.LogInformation("Search completed in {ElapsedMilliseconds}ms. Found {ResultCount} results. {Cancelled}", timer.ElapsedMilliseconds, resultCount, cancellationToken.IsCancellationRequested ? "(Cancelled)" : "");
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
				if (file.Name.Value.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
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
