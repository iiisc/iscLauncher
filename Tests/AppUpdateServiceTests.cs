using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using iscLauncher.Services;
using Xunit;

namespace iscLauncher.Tests;

public sealed class AppUpdateServiceTests : IDisposable
{
    // Spaces, an apostrophe and non-ASCII letters: the user's own folder name can contain any of them
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8], "O'Brien Ünï");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch (IOException) { }
    }

    private const string Release = """
        {
          "tag_name": "v1.1.10",
          "body": "\n- Fix things\n- Add things\n",
          "assets": [
            { "name": "checksums.txt", "browser_download_url": "https://example.com/checksums.txt", "digest": "sha256:aaaa" },
            { "name": "iscLauncher-v1.1.10-win-x64.zip", "browser_download_url": "https://example.com/app.zip", "digest": "sha256:ABCDEF" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheReleaseZipItsChecksumAndNotes()
    {
        var update = AppUpdateService.ParseRelease(Release, "1.1.9");
        Assert.True(update.UpdateAvailable);
        Assert.Equal("1.1.10", update.LatestVersion);
        Assert.Equal("iscLauncher-v1.1.10-win-x64.zip", update.AssetName);
        Assert.Equal("https://example.com/app.zip", update.DownloadUrl);
        Assert.Equal("ABCDEF", update.Sha256);
        Assert.Equal("- Fix things\n- Add things", update.Notes);
    }

    [Theory]
    [InlineData("1.1.10", false)]
    [InlineData("1.2.0", false)]
    [InlineData("1.1.9", true)]
    [InlineData("0.0.0", true)]
    public void ComparesVersionsNumerically(string current, bool updateAvailable) =>
        Assert.Equal(updateAvailable, AppUpdateService.ParseRelease(Release, current).UpdateAvailable);

    [Fact]
    public void AReleaseWithoutAChecksumHasNone()
    {
        var update = AppUpdateService.ParseRelease("""{ "tag_name": "v2.0.0", "assets": [ { "name": "a.zip", "browser_download_url": "u" } ] }""", "1.0.0");
        Assert.Null(update.Sha256);
    }

    [Fact]
    public async Task VerifyRefusesADownloadThatDoesntMatch()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "download.zip");
        File.WriteAllText(file, "the real download");
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

        await AppUpdateService.VerifyAsync(file, sha.ToLowerInvariant(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateService.VerifyAsync(file, new string('0', 64), default));
    }

    [Fact]
    public async Task TheSwapScriptInstallsTheReleaseAndCleansUp()
    {
        var appDir = Path.Combine(_root, "app");
        var currentExe = Path.Combine(appDir, "iscLauncher.exe");
        Sandbox.Write(currentExe, "old version");
        Sandbox.Write(Path.Combine(appDir, @"git\cmd\git.exe"), "old git");

        // The restarted "new version" is a harmless exe that prints the PC's name and exits
        var newExe = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        var zipPath = Path.Combine(_root, "release.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(newExe, "iscLauncher.exe");
            using var git = new StreamWriter(zip.CreateEntry("git/cmd/git.exe").Open());
            git.Write("new git");
        }

        var tempDir = Path.Combine(_root, "update temp");
        Directory.CreateDirectory(tempDir);
        var script = await AppUpdateService.PrepareSwapAsync(zipPath, tempDir, currentExe, ExitedProcessId(), default);
        RunPowerShell(script);
        WaitForRestartedApp(currentExe);

        Assert.Equal(File.ReadAllBytes(newExe), File.ReadAllBytes(currentExe));
        Assert.Equal("new git", File.ReadAllText(Path.Combine(appDir, @"git\cmd\git.exe")));
        Assert.False(Directory.Exists(tempDir));
    }

    // The script first waits for the app to exit; one that already has lets it go straight on
    private static int ExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    // Matched by path, so a real copy of the app that happens to be running is left alone
    private static void WaitForRestartedApp(string exePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                        process.WaitForExit(10_000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "the swap script didn't finish");
    }
}
