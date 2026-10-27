using System;
using System.Diagnostics;
using System.IO;
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
    public void AnUpdateReplacesTheRunningAppAndKeepsWhatItDoesntShip()
    {
        var appDir = Path.Combine(_root, "app");
        // Renamed by the user, and running: a copy of ping that waits long enough for the swap
        var currentExe = Path.Combine(appDir, "WoW Launcher.exe");
        Directory.CreateDirectory(appDir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), currentExe);
        Sandbox.Write(Path.Combine(appDir, "app.dll"), "old dll");
        Sandbox.Write(Path.Combine(appDir, @"git\cmd\git.exe"), "git from an older release");

        var release = Path.Combine(_root, "release");
        Sandbox.Write(Path.Combine(release, "iscLauncher.exe"), "new exe");
        Sandbox.Write(Path.Combine(release, "app.dll"), "new dll");
        Sandbox.Write(Path.Combine(release, @"Assets\new.png"), "new asset");

        using var running = Process.Start(new ProcessStartInfo(currentExe, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            AppUpdateService.SwapIn(release, currentExe);
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }

        Assert.Equal("new exe", File.ReadAllText(currentExe));
        Assert.False(File.Exists(Path.Combine(appDir, "iscLauncher.exe")));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(appDir, "app.dll")));
        Assert.Equal("new asset", File.ReadAllText(Path.Combine(appDir, @"Assets\new.png")));
        Assert.Equal("git from an older release", File.ReadAllText(Path.Combine(appDir, @"git\cmd\git.exe")));
        var oldDir = Path.Combine(appDir, AppUpdateService.OldVersionDir);
        Assert.Equal("old dll", File.ReadAllText(Path.Combine(oldDir, "app.dll")));
        Assert.True(File.Exists(Path.Combine(oldDir, "WoW Launcher.exe")));
    }

    [Fact]
    public void AnUpdateThatCantReplaceAFilePutsEverythingBack()
    {
        var appDir = Path.Combine(_root, "app");
        var currentExe = Path.Combine(appDir, "iscLauncher.exe");
        Sandbox.Write(currentExe, "old exe");
        Sandbox.Write(Path.Combine(appDir, "a.dll"), "old a");
        Sandbox.Write(Path.Combine(appDir, "z.dll"), "old z");

        var release = Path.Combine(_root, "release");
        Sandbox.Write(Path.Combine(release, "iscLauncher.exe"), "new exe");
        Sandbox.Write(Path.Combine(release, "a.dll"), "new a");
        Sandbox.Write(Path.Combine(release, "z.dll"), "new z");
        Sandbox.Write(Path.Combine(release, @"Assets\new.png"), "new asset");

        // Open without sharing deletes, as antivirus might hold it, so it can't be moved aside
        using (new FileStream(Path.Combine(appDir, "z.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => AppUpdateService.SwapIn(release, currentExe));

        Assert.Equal("old exe", File.ReadAllText(currentExe));
        Assert.Equal("old a", File.ReadAllText(Path.Combine(appDir, "a.dll")));
        Assert.Equal("old z", File.ReadAllText(Path.Combine(appDir, "z.dll")));
        Assert.False(File.Exists(Path.Combine(appDir, @"Assets\new.png")));
    }
}
