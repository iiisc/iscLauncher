using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using iscLauncher.Models;
using iscLauncher.Services;
using Xunit;

namespace iscLauncher.Tests;

public sealed class GameRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // Versions before the release was trimmed wrote games.json by reflection; each must read what the other writes
    [Fact]
    public async Task TheLibraryIsWrittenExactlyAsBeforeAndReadsBack()
    {
        var game = new GameEntry
        {
            Name = "Warmane",
            ExecutablePath = @"C:\WoW\Wow.exe",
            RealmlistAddress = "logon.warmane.com",
            AccountName = "account",
            RealmName = "Onyxia",
            StartupDelaySeconds = 3,
            SyncRepoUrl = "https://github.com/owner/repo.git",
            LastSynced = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc),
            LastSyncedCommit = "abc123",
            AddonSources = [new AddonSource { Repo = "owner/addon", Addons = ["Addon"], MatchedVersion = "v1", MatchedFiles = "fingerprint" }],
            DisconnectedAddonSources = ["owner/other"]
        };
        var path = Path.Combine(_root, "games.json");
        await new GameRepository(path).AddAsync(game);

        var byReflection = JsonSerializer.Serialize(new GameLibrary { Games = [game] }, new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(byReflection, File.ReadAllText(path));

        var reloaded = new GameRepository(path);
        await reloaded.LoadAsync();
        Assert.Equal(byReflection, JsonSerializer.Serialize(new GameLibrary { Games = [.. reloaded.Games] }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
