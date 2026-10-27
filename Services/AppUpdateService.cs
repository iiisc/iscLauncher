using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace iscLauncher.Services;

/// <param name="Sha256">GitHub's checksum of the download, so it can be verified before it replaces the app.</param>
public record UpdateCheckResult(bool UpdateAvailable, string LatestVersion, string? DownloadUrl, string? AssetName, string? Sha256, string Notes);

public class AppUpdateService
{
    private const string Owner = "iiisc";
    private const string Repo = "iscLauncher";
    public const string RepoUrl = $"https://github.com/{Owner}/{Repo}";

    private static readonly HttpClient _http = new();

    static AppUpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("iscLauncher");
    }

    public static string CurrentVersion
    {
        get
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct).ConfigureAwait(false);
        return ParseRelease(json, CurrentVersion);
    }

    /// <summary>Reads GitHub's latest-release JSON: the version, the zip to download, its checksum and the notes.</summary>
    internal static UpdateCheckResult ParseRelease(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "0.0.0";
        var latestVersion = tagName.TrimStart('v');

        string? downloadUrl = null;
        string? assetName = null;
        string? sha256 = null;

        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    // GitHub reports each file's checksum as "sha256:<hex>"
                    if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } value && value.StartsWith("sha256:"))
                        sha256 = value["sha256:".Length..];
                    break;
                }
            }
        }

        var notes = root.TryGetProperty("body", out var body) ? (body.GetString() ?? "").Trim() : "";
        return new UpdateCheckResult(IsNewer(latestVersion, currentVersion), latestVersion, downloadUrl, assetName, sha256, notes);
    }

    /// <summary>Where the files an update replaced are moved, since they're in use until the app restarts.</summary>
    internal const string OldVersionDir = "iscLauncher-old";

    /// <summary>
    /// Downloads and checks the release, then puts it in place of the running app. Returns the exe to start before this
    /// one exits. If it fails, the app is left as it was.
    /// </summary>
    public async Task<string> DownloadAndApplyAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // The update replaces the app itself, so a download that can't be verified isn't installed
        if (update.Sha256 == null)
            throw new InvalidOperationException("this release has no checksum to verify it against. Download it from GitHub instead.");

        var tempDir = Path.Combine(Path.GetTempPath(), "iscLauncherUpdate");
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, update.AssetName!);
            await DownloadAsync(update.DownloadUrl!, zipPath, progress, ct).ConfigureAwait(false);
            await VerifyAsync(zipPath, update.Sha256, ct).ConfigureAwait(false);

            var releaseDir = Path.Combine(tempDir, "release");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, releaseDir), ct).ConfigureAwait(false);
            var currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("the app couldn't find its own exe.");
            // Not cancellable: it's quick, and stopping halfway would need the same undoing as a failure
            await Task.Run(() => SwapIn(releaseDir, currentExe), CancellationToken.None).ConfigureAwait(false);
            return currentExe;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Moves the release's files into the app's folder. Windows won't let a running program's files be overwritten, but it
    /// does let them be moved, so each one being replaced goes into <see cref="OldVersionDir"/> first, for the next start to
    /// delete. If anything fails, every file is put back.
    /// </summary>
    internal static void SwapIn(string releaseDir, string currentExe)
    {
        if (!File.Exists(Path.Combine(releaseDir, "iscLauncher.exe")))
            throw new FileNotFoundException("The update package has no iscLauncher.exe.");
        var appDir = Path.GetDirectoryName(currentExe)!;
        var oldDir = Path.Combine(appDir, OldVersionDir);
        // Left by an earlier update; that version isn't running, so its files can go
        if (Directory.Exists(oldDir))
            Directory.Delete(oldDir, true);

        var replaced = new List<(string File, string MovedTo)>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(releaseDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(releaseDir, file);
                // A copy of the app the user renamed keeps its name
                var target = name == "iscLauncher.exe" ? currentExe : Path.Combine(appDir, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var movedTo = Path.Combine(oldDir, Path.GetRelativePath(appDir, target));
                    Directory.CreateDirectory(Path.GetDirectoryName(movedTo)!);
                    File.Move(target, movedTo);
                    replaced.Add((target, movedTo));
                }
                else
                    added.Add(target);
                File.Move(file, target);
            }
        }
        catch
        {
            foreach (var file in added)
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var (file, movedTo) in replaced)
            {
                try
                {
                    File.Delete(file);
                    File.Move(movedTo, file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    internal static async Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = File.Create(destPath);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int lastReportedPct = -1;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            bytesRead += read;
            if (totalBytes > 0)
            {
                var pct = (int)((double)bytesRead / totalBytes * 100);
                if (pct != lastReportedPct)
                {
                    lastReportedPct = pct;
                    progress?.Report((double)bytesRead / totalBytes);
                }
            }
        }
    }

    internal static async Task VerifyAsync(string path, string sha256, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the download doesn't match GitHub's checksum, so nothing was installed. Try again.");
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var lv) && Version.TryParse(current, out var cv))
            return lv > cv;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
