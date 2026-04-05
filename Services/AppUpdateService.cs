using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace iscLauncher.Services;

public record UpdateCheckResult(bool UpdateAvailable, string LatestVersion, string? DownloadUrl, string? AssetName);

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
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "0.0.0";
        var latestVersion = tagName.TrimStart('v');

        string? downloadUrl = null;
        string? assetName = null;

        if (root.TryGetProperty("assets", out var assets))
        {
            string? zipUrl = null, zipName = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var browserUrl = asset.GetProperty("browser_download_url").GetString();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = browserUrl;
                    assetName = name;
                    break;
                }
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = browserUrl;
                    zipName = name;
                }
            }
            if (downloadUrl == null && zipUrl != null)
            {
                downloadUrl = zipUrl;
                assetName = zipName;
            }
        }

        return new UpdateCheckResult(IsNewer(latestVersion, CurrentVersion), latestVersion, downloadUrl, assetName);
    }

    // Downloads the release asset and writes the swap script, then launches it.
    // The caller is responsible for calling Application.Current.Exit() on the UI thread.
    public async Task DownloadAndApplyAsync(string downloadUrl, string assetName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "iscLauncherUpdate");
        Directory.CreateDirectory(tempDir);
        var destPath = Path.Combine(tempDir, assetName);

        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
        fileStream.Close();

        var currentExe = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

        string newExePath;
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(tempDir, "extracted");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(destPath, extractDir), ct).ConfigureAwait(false);
            newExePath = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new FileNotFoundException("No executable found in the update package.");
        }
        else
        {
            newExePath = destPath;
        }

        var scriptPath = Path.Combine(tempDir, "update.ps1");
        await File.WriteAllTextAsync(scriptPath,
            $"Start-Sleep -Milliseconds 1500\r\n" +
            $"Copy-Item -Path '{newExePath}' -Destination '{currentExe}' -Force\r\n" +
            $"Start-Process -FilePath '{currentExe}'\r\n" +
            $"Remove-Item -Path '{tempDir}' -Recurse -Force -ErrorAction SilentlyContinue\r\n",
            ct).ConfigureAwait(false);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        });
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var lv) && Version.TryParse(current, out var cv))
            return lv > cv;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
