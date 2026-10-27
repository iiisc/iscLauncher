using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using iscLauncher.Services;
using Xunit;

namespace iscLauncher.Tests;

public sealed class GitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ADownloadedGitIsInstalledOnlyIfItMatchesItsChecksum()
    {
        var target = Path.Combine(_root, "Git");
        Sandbox.Write(Path.Combine(target, @"cmd\git.exe"), "old git");
        var zip = Path.Combine(_root, "MinGit.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var git = new StreamWriter(archive.CreateEntry("cmd/git.exe").Open()))
            git.Write("new git");

        await Assert.ThrowsAsync<InvalidDataException>(() => GitService.InstallZipAsync(zip, new string('0', 64), target, default));
        Assert.Equal("old git", File.ReadAllText(Path.Combine(target, @"cmd\git.exe")));

        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        await GitService.InstallZipAsync(zip, sha, target, default);
        Assert.Equal("new git", File.ReadAllText(Path.Combine(target, @"cmd\git.exe")));
        Assert.False(Directory.Exists(target + ".partial"));
    }
}
