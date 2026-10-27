using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    // Downloads the release asset and writes the swap script, then launches it.
    // The caller is responsible for calling Application.Current.Exit() on the UI thread.
    public async Task DownloadAndApplyAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // The update replaces the app itself, so a download that can't be verified isn't installed
        if (update.Sha256 == null)
            throw new InvalidOperationException("this release has no checksum to verify it against. Download it from GitHub instead.");

        var tempDir = Path.Combine(Path.GetTempPath(), "iscLauncherUpdate");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, update.AssetName!);

        await DownloadAsync(update.DownloadUrl!, zipPath, progress, ct).ConfigureAwait(false);
        await VerifyAsync(zipPath, update.Sha256, ct).ConfigureAwait(false);

        var currentExe = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        var scriptPath = await PrepareSwapAsync(zipPath, tempDir, currentExe, Environment.ProcessId, ct).ConfigureAwait(false);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        });
    }

    private static async Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
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

    /// <summary>
    /// Unpacks the release next to it and writes the script that, once process <paramref name="processId"/> has exited,
    /// copies it over the app folder and restarts <paramref name="currentExe"/>. Returns the script's path.
    /// </summary>
    internal static async Task<string> PrepareSwapAsync(string zipPath, string tempDir, string currentExe, int processId, CancellationToken ct)
    {
        var extractDir = Path.Combine(tempDir, "extracted");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), ct).ConfigureAwait(false);
        // By name: the release has other exes beside the app
        var newExePath = Path.Combine(extractDir, "iscLauncher.exe");
        if (!File.Exists(newExePath))
            throw new FileNotFoundException("The update package has no iscLauncher.exe.");
        var appDir = Path.GetDirectoryName(currentExe)!;

        // The script runs after this process exits, so it must report its own failure: otherwise the user is left
        // on the old version (or with no app running) and never finds out why
        var scriptPath = Path.Combine(tempDir, "update.ps1");
        var script = $$"""
            Wait-Process -Id {{processId}} -Timeout 30 -ErrorAction SilentlyContinue
            $copied = $false
            for ($i = 0; $i -lt 10 -and -not $copied; $i++) {
                try {
                    # Everything else in the release (the bundled Git) goes next to the app; robocopy merges folders
                    robocopy {{PsQuote(extractDir)}} {{PsQuote(appDir)}} /E /XF {{PsQuote(Path.GetFileName(newExePath))}} /NJH /NJS /NFL /NDL /NP | Out-Null
                    if ($LASTEXITCODE -ge 8) { throw "robocopy failed" }
                    Copy-Item -LiteralPath {{PsQuote(newExePath)}} -Destination {{PsQuote(currentExe)}} -Force -ErrorAction Stop
                    $copied = $true
                }
                catch { Start-Sleep -Milliseconds 500 }
            }
            if (-not $copied) {
                Add-Type -AssemblyName PresentationFramework
                [System.Windows.MessageBox]::Show("The update couldn't be installed, so the current version will start instead. Try again from Options, or download the new version from GitHub.", 'iscLauncher update') | Out-Null
            }
            Start-Process -FilePath {{PsQuote(currentExe)}}
            if ($copied) { Remove-Item -LiteralPath {{PsQuote(tempDir)}} -Recurse -Force -ErrorAction SilentlyContinue }
            """;
        // Windows PowerShell reads BOM-less files as ANSI, which would mangle non-ASCII characters in the paths
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, ct).ConfigureAwait(false);
        return scriptPath;
    }

    // Single-quoted PowerShell strings are literal; a doubled quote escapes an apostrophe (e.g. C:\Users\O'Brien)
    private static string PsQuote(string value) => $"'{value.Replace("'", "''")}'";

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var lv) && Version.TryParse(current, out var cv))
            return lv > cv;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
