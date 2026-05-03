using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using Ardalis.GuardClauses;
using CliWrap;
using Godot;
using Microsoft.CodeAnalysis;
using NuGet.Versioning;
using Octokit;
using Environment = System.Environment;
using HttpClient = System.Net.Http.HttpClient;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace SharpIDE.Godot.Features.IdeAutoUpdate;

public static class AutoUpdate
{
    public static async Task<Release?> CheckForUpdates(DateTimeOffset? lastChecked)
    {
        var requiresCheck = lastChecked is null || (DateTimeOffset.UtcNow - lastChecked) > TimeSpan.FromMinutes(5);
        if (!requiresCheck) return null;
        try
        {
            var latestRelease = await GetLatestRelease();
            var latestVersion = NuGetVersion.Parse(latestRelease.Name[1..]); // remove 'v' prefix
            var currentVersion = Singletons.SharpIdeVersion;
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
    private static readonly string UncompressedArchiveExtension = OsIdentifier switch
    {
        "osx" => ".zip",
        "linux" => ".tar",
        "win" => ".zip",
        _ => throw new ArgumentOutOfRangeException()
    };
    private static readonly string CompressedArchiveExtension = OsIdentifier switch
    {
        "osx" => ".zip",
        "linux" => ".tar.gz",
        "win" => ".zip",
        _ => throw new ArgumentOutOfRangeException()
    };
    
    public static async Task<string> EnsureReleaseZipReadyForSwap(Release release, IProgress<(long currentBytes, long totalBytes)>? progress = null)
    {
        Directory.CreateDirectory(UpdateTempPath);
        Directory.CreateDirectory(Path.Combine(UpdateTempPath, "raw"));
        Directory.CreateDirectory(Path.Combine(UpdateTempPath, "ready"));
        var uncompressedReleaseArchive = new FileInfo(Path.Combine(UpdateTempPath, "ready", $"{ReleaseAssetNamePrefix}{release.Name[1..]}{UncompressedArchiveExtension}"));
        if (uncompressedReleaseArchive.Exists is false)
        {
            await CreateUncompressedArchiveForRelease(release, uncompressedReleaseArchive, progress);
            uncompressedReleaseArchive.Refresh();
            if (uncompressedReleaseArchive.Exists is false) throw new InvalidOperationException($"Failed to prepare release archive for swap: uncompressed archive was not created successfully at {uncompressedReleaseArchive.FullName}");
        }

        // Pre-build the updater cs file based app
        await Cli.Wrap("dotnet").WithArguments(["build", Path.Combine(AppContext.BaseDirectory, "update-sharpide.cs")]).ExecuteAsync();
        return uncompressedReleaseArchive.FullName;
    }

    private static async Task CreateUncompressedArchiveForRelease(Release release, FileInfo uncompressedReleaseArchiveToCreate, IProgress<(long currentBytes, long totalBytes)>? progress = null)
    {
        // remove the 'v' prefix from the release name
        var compressedReleaseArchive = new FileInfo(Path.Combine(UpdateTempPath, "raw", $"{ReleaseAssetNamePrefix}{release.Name[1..]}{CompressedArchiveExtension}"));
        if (compressedReleaseArchive.Exists is false)
        {
            await DownloadRelease(release, progress);
            compressedReleaseArchive.Refresh();
            if (compressedReleaseArchive.Exists is false) throw new InvalidOperationException($"Release archive was not downloaded successfully to {compressedReleaseArchive.FullName}");
        }
        await using var compressedArchiveStream = compressedReleaseArchive.OpenRead();

        if (OperatingSystem.IsLinux())
        {
            await using var gz = new GZipStream(compressedArchiveStream, CompressionMode.Decompress, leaveOpen: true);
            await using var uncompressedTarFileStream = uncompressedReleaseArchiveToCreate.Create();
            await gz.CopyToAsync(uncompressedTarFileStream);
        }
        else if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            await using var compressedZipArchive = await ZipArchive.CreateAsync(compressedArchiveStream, ZipArchiveMode.Read, leaveOpen: true, null);
            await using var uncompressedArchiveStream = uncompressedReleaseArchiveToCreate.Create();
            await using var uncompressedZipArchive = new ZipArchive(uncompressedArchiveStream, ZipArchiveMode.Create);
        
            foreach (var entry in compressedZipArchive.Entries)
            {
                var newEntry = uncompressedZipArchive.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                newEntry.ExternalAttributes = entry.ExternalAttributes;
                await using var entryStream = await entry.OpenAsync();
                await using var newEntryStream = await newEntry.OpenAsync();
                await entryStream.CopyToAsync(newEntryStream);
            }
            await uncompressedArchiveStream.FlushAsync();
        }
    }

    private static async Task DownloadRelease(Release release, IProgress<(long currentBytes, long totalBytes)>? progress = null)
    {
        var asset = release.Assets.SingleOrDefault(a => a.Name.StartsWith(ReleaseAssetNamePrefix, StringComparison.OrdinalIgnoreCase));
        Guard.Against.Null(asset);
        var assetFileName = asset.Name;
        var assetSizeBytes = asset.Size;
        var releaseArchive = new FileInfo(Path.Combine(UpdateTempPath, "raw", assetFileName));
        if (releaseArchive.Exists) throw new InvalidOperationException($"Release archive already exists at {releaseArchive.FullName}"); // TODO: don't throw?
        using var httpClient = new HttpClient();
        
        // our CopyToWithProgressAsync extension does not and should not know about the total bytes, so we use an intermediate Progress for it to report the current total bytes it has read
        var intermediateProgress = new Progress<long>(totalBytesRead => progress?.Report((totalBytesRead, assetSizeBytes)));
        
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(new System.Net.Http.Headers.ProductHeaderValue("SharpIDE-AutoUpdate")));
        using var response = await httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = releaseArchive.Create();
        await contentStream.CopyToWithProgressAsync(fileStream, 81920, intermediateProgress);
        await contentStream.FlushAsync();
        await fileStream.FlushAsync();
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

    public static async Task StartUpdaterProcess(string uncompressedReleaseArchiveFilePath)
    {
        var currentProcessExecutablePath = OS.GetExecutablePath();
        List<string> args = [Path.Combine(AppContext.BaseDirectory, "update-sharpide.cs"), "--no-build", "--", Path.GetDirectoryName(currentProcessExecutablePath)!, currentProcessExecutablePath, uncompressedReleaseArchiveFilePath, Environment.ProcessId.ToString()];
        ProcessStartInfo processStartInfo = null!;
        if (OperatingSystem.IsWindows())
        {
            processStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processStartInfo.ArgumentList.AddRange(args);
        }
        else if (OperatingSystem.IsLinux())
        {
            processStartInfo = new ProcessStartInfo
            {
                FileName = "setsid",
                //ArgumentList = { "x-terminal-emulator", "-e", "dotnet" },
                ArgumentList = { "dotnet" },
                UseShellExecute = false
            };
            processStartInfo.ArgumentList.AddRange(args);
        }
        if (OperatingSystem.IsMacOS())
        {
            processStartInfo = new ProcessStartInfo
            {
                FileName = "nohup",
                ArgumentList = { "dotnet" },
                UseShellExecute = true
            };
            processStartInfo.ArgumentList.AddRange(args);
        }
        
        Process.Start(processStartInfo);
        await Task.Delay(500);
    }
}