using System;
using System.Diagnostics;
using System.IO;
using iscLauncher.Models;
using iscLauncher.Services;
using Xunit;

namespace iscLauncher.Tests;

public sealed class GameLauncherServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void ReadsARunningProcesssExe() =>
        Assert.Equal(Environment.ProcessPath, GameLauncherService.ImagePath(Environment.ProcessId), ignoreCase: true);

    // Two clients share the exe name; only the one whose own exe is running counts as running
    [Fact]
    public void OnlyTheGameWhoseExeIsRunningCountsAsRunning()
    {
        var running = Path.Combine(_root, "Warmane", "Wow.exe");
        var idle = Path.Combine(_root, "Classic", "Wow.exe");
        foreach (var exe in new[] { running, idle })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            // Pinging for a while keeps it running through the check
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        }

        using var process = Process.Start(new ProcessStartInfo(running, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var launcher = new GameLauncherService(new CredentialService());
            Assert.True(launcher.IsRunning(new GameEntry { ExecutablePath = running }));
            Assert.False(launcher.IsRunning(new GameEntry { ExecutablePath = idle }));
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }
}
