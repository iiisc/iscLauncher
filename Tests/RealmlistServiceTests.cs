using System;
using System.IO;
using System.Threading.Tasks;
using iscLauncher.Models;
using iscLauncher.Services;
using Xunit;

namespace iscLauncher.Tests;

public sealed class RealmlistServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RealmlistService _service = new();

    private string Exe => Path.Combine(_root, "Wow.exe");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ConfigIsCreatedOnAFreshInstallAndUpdatedInPlace()
    {
        var game = new GameEntry { ExecutablePath = Exe, AccountName = "bob", RealmName = "Foo$0Bar", RealmlistAddress = "logon.example.com" };
        await _service.ApplyAsync(game);
        await _service.ApplyAsync(game);

        var lines = File.ReadAllLines(Path.Combine(_root, @"WTF\Config.wtf"));
        Assert.Single(lines, l => l.StartsWith("SET accountName"));
        // A regex replacement string would have expanded $0
        Assert.Contains("SET realmName \"Foo$0Bar\"", lines);
    }

    [Fact]
    public async Task RealmlistGoesIntoEachLocaleThatHasOne()
    {
        Sandbox.Write(Path.Combine(_root, @"Data\deDE\realmlist.wtf"), "set realmlist old");
        Directory.CreateDirectory(Path.Combine(_root, @"Data\frFR"));

        await _service.ApplyAsync(new GameEntry { ExecutablePath = Exe, RealmlistAddress = "logon.example.com" });

        Assert.Equal("set realmlist logon.example.com", File.ReadAllText(Path.Combine(_root, @"Data\deDE\realmlist.wtf")));
        Assert.False(File.Exists(Path.Combine(_root, @"Data\frFR\realmlist.wtf")));
        Assert.False(Directory.Exists(Path.Combine(_root, @"Data\enUS")));
    }

    [Fact]
    public async Task FailuresAreReportedAsAppException()
    {
        Sandbox.Write(Path.Combine(_root, "WTF"), "a file where the folder should be");
        await Assert.ThrowsAsync<AppException>(() => _service.ApplyAsync(new GameEntry { ExecutablePath = Exe, AccountName = "bob" }));
    }
}
