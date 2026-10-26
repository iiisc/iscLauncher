using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using iscLauncher.Services;
using Xunit;
using static iscLauncher.Tests.Sandbox;

namespace iscLauncher.Tests;

public sealed class AddonFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { AddonFiles.ForceDeleteDirectory(_root); } catch (IOException) { }
    }

    /// <summary>A newer client's WTF layout: shared SavedVariables beside a numbered account, characters under a numbered group.</summary>
    private string ModernGame()
    {
        var game = Path.Combine(_root, "game");
        Write(Path.Combine(game, @"WTF\Account\SavedVariables\Blizzard_SavedSets.lua"), "shared");
        Write(Path.Combine(game, @"WTF\Account\123#1\SavedVariables\Questie.lua"), "account");
        Write(Path.Combine(game, @"WTF\Account\123#1\SavedVariables\Questie.lua.bak"), "old");
        Write(Path.Combine(game, @"WTF\Account\123#1\bindings-cache.wtf"), "bindings");
        Write(Path.Combine(game, @"WTF\Account\123#1\1\Dunder-Isc\AddOns.txt"), "WhatsTraining: enabled");
        Write(Path.Combine(game, @"WTF\Account\123#1\1\Blue-Isc\AddOns.txt"), "nothing");
        return game;
    }

    private static string[] Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).ToArray();

    [Fact]
    public void FindsAccountsAndCharactersInANewerLayout()
    {
        var game = ModernGame();
        Assert.EndsWith("123#1", AddonFiles.FindAccountDir(game, null));
        Assert.Equal(["Blue-Isc", "Dunder-Isc"], AddonFiles.Characters(game).Select(c => c.Label).Order());
    }

    [Fact]
    public void AddonSettingsAreSharedAndAccountSavedVariablesOnly()
    {
        var game = ModernGame();
        var copy = Path.Combine(_root, "copy");
        AddonFiles.CopyWtf(AddonFiles.WtfAccountDir(game), AddonFiles.WtfAccountDir(copy), new SyncOptions(true, false), mirror: false, default);
        Assert.Equal([@"WTF\Account\123#1\SavedVariables\Questie.lua", @"WTF\Account\SavedVariables\Blizzard_SavedSets.lua"], Files(copy).Order());
    }

    [Fact]
    public void CharacterSettingsAreEverythingElseInTheAccount()
    {
        var game = ModernGame();
        var copy = Path.Combine(_root, "copy");
        AddonFiles.CopyWtf(AddonFiles.WtfAccountDir(game), AddonFiles.WtfAccountDir(copy), new SyncOptions(false, true), mirror: false, default);
        Assert.Equal([@"WTF\Account\123#1\1\Blue-Isc\AddOns.txt", @"WTF\Account\123#1\1\Dunder-Isc\AddOns.txt", @"WTF\Account\123#1\bindings-cache.wtf"],
            Files(copy).Order());
    }

    [Fact]
    public void CharacterSetupCopiesAddonDataButNotBlizzardsOwn()
    {
        var characters = AddonFiles.Characters(ModernGame());
        var dunder = characters.Single(c => c.Label == "Dunder-Isc").Dir;
        var blue = characters.Single(c => c.Label == "Blue-Isc").Dir;
        Write(Path.Combine(dunder, @"SavedVariables\WhatsTraining.lua"), "dunder data");
        Write(Path.Combine(dunder, @"SavedVariables\Blizzard_RaidUI.lua"), "dunder blizzard");

        var data = AddonFiles.CharacterAddonData(dunder);
        Assert.Equal(["WhatsTraining"], data);
        AddonFiles.CopyCharacterSetup(dunder, blue, enabledAddons: true, data);
        Assert.Equal("WhatsTraining: enabled", File.ReadAllText(Path.Combine(blue, "AddOns.txt")));
        Assert.Equal("dunder data", File.ReadAllText(Path.Combine(blue, @"SavedVariables\WhatsTraining.lua")));
        Assert.False(File.Exists(Path.Combine(blue, @"SavedVariables\Blizzard_RaidUI.lua")));
    }

    [Fact]
    public void RestoringABackupPutsBackExactlyWhatItHolds()
    {
        var game = Path.Combine(_root, "game");
        Write(Path.Combine(game, @"WTF\Account\ACC\SavedVariables\Foo.lua"), "original settings");
        Write(Path.Combine(game, @"Interface\AddOns\Foo\Foo.toc"), "original foo");
        var backups = Path.Combine(_root, "backups");
        AddonFiles.Backup(backups, game, ["Foo"], includeWtf: true, default);
        var zipPath = Assert.Single(Directory.GetFiles(backups));
        var (addons, hasSettings) = AddonFiles.BackupContents(zipPath);
        Assert.True(hasSettings);
        Assert.Equal(["Foo"], addons);

        Write(Path.Combine(game, @"WTF\Account\ACC\SavedVariables\Foo.lua"), "changed");
        Write(Path.Combine(game, @"WTF\Account\ACC\SavedVariables\New.lua"), "added later");
        Write(Path.Combine(game, @"Interface\AddOns\Foo\Extra.lua"), "added later");
        Write(Path.Combine(game, @"Interface\AddOns\Bar\Bar.toc"), "new addon");
        AddonFiles.RestoreBackup(zipPath, game, default);

        Assert.Equal("original settings", File.ReadAllText(Path.Combine(game, @"WTF\Account\ACC\SavedVariables\Foo.lua")));
        Assert.False(File.Exists(Path.Combine(game, @"WTF\Account\ACC\SavedVariables\New.lua")));
        Assert.False(File.Exists(Path.Combine(game, @"Interface\AddOns\Foo\Extra.lua")));
        Assert.Equal("original foo", File.ReadAllText(Path.Combine(game, @"Interface\AddOns\Foo\Foo.toc")));
        // The backup has nothing to say about an addon installed after it
        Assert.True(File.Exists(Path.Combine(game, @"Interface\AddOns\Bar\Bar.toc")));
    }

    [Fact]
    public void RestoreReadsTheOldLayoutAndNeverWritesOutsideTheGame()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var zipPath = Path.Combine(_root, "old.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("Account/ACC/SavedVariables/Foo.lua").Open())) w.Write("old format");
            using (var w = new StreamWriter(zip.CreateEntry("AddOns/../../escaped.txt").Open())) w.Write("bad");
        }
        AddonFiles.RestoreBackup(zipPath, game, default);

        Assert.Equal("old format", File.ReadAllText(Path.Combine(game, @"WTF\Account\ACC\SavedVariables\Foo.lua")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }
}
