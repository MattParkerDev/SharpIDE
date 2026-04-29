using System.IO.Compression;
using System.Net.Http.Headers;
using Ardalis.GuardClauses;
using Godot;
using NuGet.Versioning;
using Octokit;
using Environment = System.Environment;
using HttpClient = System.Net.Http.HttpClient;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace SharpIDE.Godot.Features.IdeAutoUpdate;

public class AutoUpdate
{
    private string _currentVersion = "0.1.24"; // TODO: Replace
    public async Task<Release?> CheckForUpdates(DateTimeOffset? lastChecked)
    {
        var requiresCheck = lastChecked is null || (DateTimeOffset.UtcNow - lastChecked) > TimeSpan.FromHours(20);
        if (!requiresCheck) return null;
        try
        {
            var latestRelease = await GetLatestRelease();
            var latestVersion = NuGetVersion.Parse(latestRelease.Name[1..]); // remove 'v' prefix
            var currentVersion = NuGetVersion.Parse(_currentVersion);
            if (latestVersion <= currentVersion) return null;
            return latestRelease;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to check for updates: {ex}");
            return null;
        }
    }
    private static readonly string UpdateTempPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SharpIDE", "UpdateTemp");
    private static readonly string OsIdentifier = OS.GetName() switch
    {
        "Windows" => "win",
        "macOS" => "osx",
        "Linux" => "linux",
        _ => throw new ArgumentOutOfRangeException(),
    };
    private static readonly string Architecture = OsIdentifier switch
    {
        "osx" => "universal",
        "linux" => "x64",
        "win" => Engine.GetArchitectureName() switch
        {
            "x86_64" => "x64",
            "arm64" => "arm64",
            _ => throw new ArgumentOutOfRangeException()
        },
        _ => throw new ArgumentOutOfRangeException()
    };
    private static readonly string ReleaseAssetNamePrefix = $"sharpide-{OsIdentifier}-{Architecture}-";
    
    public async Task<string> EnsureReleaseZipReadyForSwap(Release release)
    {
        Directory.CreateDirectory(UpdateTempPath);
        var uncompressedReleaseArchive = new FileInfo(Path.Combine(UpdateTempPath, "ready", $"{ReleaseAssetNamePrefix}{release.Name[1..]}.zip"));
        if (uncompressedReleaseArchive.Exists is false)
        {
            await CreateUncompressedArchiveForRelease(release, uncompressedReleaseArchive);
            uncompressedReleaseArchive.Refresh();
            if (uncompressedReleaseArchive.Exists is false) throw new InvalidOperationException($"Failed to prepare release archive for swap: uncompressed archive was not created successfully at {uncompressedReleaseArchive.FullName}");
        }
        return uncompressedReleaseArchive.FullName;
    }

    private async Task CreateUncompressedArchiveForRelease(Release release, FileInfo uncompressedReleaseArchiveToCreate)
    {
        // remove the 'v' prefix from the release name
        var compressedReleaseArchive = new FileInfo(Path.Combine(UpdateTempPath, "raw", $"{ReleaseAssetNamePrefix}{release.Name[1..]}.zip"));
        if (compressedReleaseArchive.Exists is false)
        {
            await DownloadRelease(release);
            compressedReleaseArchive.Refresh();
            if (compressedReleaseArchive.Exists is false) throw new InvalidOperationException($"Release archive was not downloaded successfully to {compressedReleaseArchive.FullName}");
        }
        await using var compressedArchiveStream = compressedReleaseArchive.OpenRead();
        await using var compressedZipArchive = await ZipArchive.CreateAsync(compressedArchiveStream, ZipArchiveMode.Read, leaveOpen: false, null);
        await using var uncompressedArchiveStream = uncompressedReleaseArchiveToCreate.Create();
        await using var uncompressedZipArchive = new ZipArchive(uncompressedArchiveStream, ZipArchiveMode.Create);
        
        foreach (var entry in compressedZipArchive.Entries)
        {
            var newEntry = uncompressedZipArchive.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
            await using var entryStream = await entry.OpenAsync();
            await using var newEntryStream = await newEntry.OpenAsync();
            await entryStream.CopyToAsync(newEntryStream);
            await newEntryStream.FlushAsync();
        }
        await uncompressedArchiveStream.FlushAsync();
    }

    private static async Task DownloadRelease(Release release)
    {
        var appdataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var updateTemp = Path.Combine(appdataFolderPath, "SharpIDE", "UpdateTemp");
        Directory.CreateDirectory(updateTemp);
        
        var asset = release.Assets.SingleOrDefault(a => a.Name.StartsWith(ReleaseAssetNamePrefix, StringComparison.OrdinalIgnoreCase));
        Guard.Against.Null(asset);
        var assetFileName = asset.Name;
        var releaseArchive = new FileInfo(Path.Combine(updateTemp, "raw", assetFileName));
        if (releaseArchive.Exists) throw new InvalidOperationException($"Release archive already exists at {releaseArchive.FullName}"); // TODO: don't throw?
        //var downloadUrl = asset.BrowserDownloadUrl;
        //var gitHubClient = GetGitHubClient();
        //var responseRaw = await gitHubClient.Connection.Get<byte[]>(new Uri(asset.Url), (Dictionary<string, string>)[], "application/octet-stream");
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(new System.Net.Http.Headers.ProductHeaderValue("SharpIDE-AutoUpdate")));
        using var response = await httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = releaseArchive.Create();
        await contentStream.CopyToAsync(fileStream);
        await contentStream.FlushAsync();
        await fileStream.FlushAsync();

        //var test = await ZipArchive.CreateAsync(fileStream, ZipArchiveMode.Read, leaveOpen: false, null);
        
    }

    private static async Task<Release> GetLatestRelease()
    {
        var gitHubClient = GetGitHubClient();
        const string owner = "MattParkerDev";
        const string repo = "SharpIDE";
        
        var release = await gitHubClient.Repository.Release.GetLatest(owner, repo);
        return release;
    }

    private static GitHubClient GetGitHubClient()
    {
        return new GitHubClient(new ProductHeaderValue("SharpIDE-AutoUpdate"));
    }
}