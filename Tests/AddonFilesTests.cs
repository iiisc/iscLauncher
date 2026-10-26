using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using iscLauncher.Models;
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
    public void ApplyingReplacesAReadOnlyAddonFolder()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Addon(source, "Foo", "2.0");
        Addon(target, "Foo", "1.0");
        var folder = Path.Combine(AddonFiles.GameAddOnsDir(target), "Foo");
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);

        var preview = AddonFiles.Compare(AddonFiles.GameAddOnsDir(source), AddonFiles.GameAddOnsDir(target), false, 0, default);
        AddonFiles.ApplyAddons(AddonFiles.GameAddOnsDir(source), AddonFiles.GameAddOnsDir(target), preview.Diffs, default);

        Assert.Contains("2.0", File.ReadAllText(Path.Combine(folder, "Foo.toc")));
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

    [Theory]
    [InlineData("https://github.com/Zidras/DBM-Warmane/", "Zidras/DBM-Warmane")]
    [InlineData("github.com/NoM0Re/WeakAuras-WotLK", "NoM0Re/WeakAuras-WotLK")]
    [InlineData("  https://github.com/a/b.git ", "a/b")]
    [InlineData("https://github.com/a/b/tree/main/Sub", "a/b")]
    [InlineData("a/My.Addon", "a/My.Addon")]
    [InlineData("## X-Website: github.com/fondlez", null)]
    [InlineData("https://gitlab.com/a/b", null)]
    public void ReadsARepoFromAGitHubLink(string text, string? repo) => Assert.Equal(repo, AddonFiles.GitHubRepo(text));

    [Fact]
    public void ReadsTheRepoAnAddonNamesAsItsHome()
    {
        var addOns = Path.Combine(_root, "AddOns");
        Write(Path.Combine(addOns, @"DBM-Core\DBM-Core.toc"), "## Interface: 30300\n## Version: 10.1\n## X-Website: github.com/Zidras/DBM-Warmane/\n");
        Write(Path.Combine(addOns, @"Author\Author.toc"), "## X-Website: github.com/fondlez\n");
        // Only the header counts; a file listed below it isn't a link
        Write(Path.Combine(addOns, @"Plain\Plain.toc"), "## Title: Plain\nlibs/github.com/x/y.lua\n");

        Assert.Equal(("10.1", "Zidras/DBM-Warmane"), AddonFiles.AddonInfo(Path.Combine(addOns, "DBM-Core")));
        Assert.Null(AddonFiles.AddonInfo(Path.Combine(addOns, "Author")).Repo);
        Assert.Null(AddonFiles.AddonInfo(Path.Combine(addOns, "Plain")).Repo);
    }

    [Fact]
    public void TheFingerprintChangesWhenAFileIsRewrittenAddedOrRemoved()
    {
        var addOns = Path.Combine(_root, "AddOns");
        Write(Path.Combine(addOns, @"Foo\Foo.toc"), "## Version: 1");
        Write(Path.Combine(addOns, @"Other\Other.toc"), "not part of it");
        var first = AddonFiles.Fingerprint(addOns, ["Foo", "Missing"]);
        Assert.Equal(first, AddonFiles.Fingerprint(addOns, ["Foo", "Missing"]));

        Write(Path.Combine(addOns, @"Other\Other.toc"), "changed, but not one of the addons asked about");
        Assert.Equal(first, AddonFiles.Fingerprint(addOns, ["Foo"]));

        File.SetLastWriteTimeUtc(Path.Combine(addOns, @"Foo\Foo.toc"), DateTime.UtcNow.AddMinutes(1));
        var rewritten = AddonFiles.Fingerprint(addOns, ["Foo"]);
        Assert.NotEqual(first, rewritten);
        Write(Path.Combine(addOns, @"Foo\New.lua"), "");
        Assert.NotEqual(rewritten, AddonFiles.Fingerprint(addOns, ["Foo"]));
    }

    [Fact]
    public void AddonsAreConnectedToTheReposTheirTocNamesUnlessDisconnected()
    {
        var game = new GameEntry
        {
            AddonSources = [new AddonSource { Repo = "me/Picked", Addons = ["Clique"] }],
            DisconnectedAddonSources = ["wrong/Retail"]
        };
        var installed = new Dictionary<string, (string? Version, string? Repo)>
        {
            ["Clique"] = ("1", null),
            ["DBM-Core"] = ("10", "Zidras/DBM-Warmane"),
            ["DBM-GUI"] = ("10", "zidras/dbm-warmane"),
            ["WeakAuras"] = ("5", "wrong/Retail")
        };

        var sources = AddonUpdateService.Sources(game, installed);
        Assert.Equal(["me/Picked", "Zidras/DBM-Warmane"], sources.Select(s => s.Repo));
        Assert.Equal(["DBM-Core", "DBM-GUI"], sources[1].Addons);
        // Found sources aren't kept until a check saves them
        Assert.Single(game.AddonSources);
    }

    [Fact]
    public void UnpacksAddonsWhereverTheDownloadKeepsThem()
    {
        // GitHub's code zip: a folder named after the commit, holding addon folders that bring their own libraries
        var codeZip = Zip("code.zip",
            ("Owner-Pack-1a2b3c/README.md", ""),
            ("Owner-Pack-1a2b3c/Pack-Core/Pack-Core.toc", "## Version: 2"),
            ("Owner-Pack-1a2b3c/Pack-Core/Libs/LibStub/LibStub.toc", ""),
            ("Owner-Pack-1a2b3c/Pack-Raids/Pack-Raids_Wrath.toc", ""));
        var code = Path.Combine(_root, "code");
        Assert.Equal(["Pack-Core", "Pack-Raids"], AddonFiles.ExtractAddons(codeZip, code).Order());
        Assert.True(File.Exists(Path.Combine(code, @"Pack-Core\Libs\LibStub\LibStub.toc")));
        Assert.Equal(["Pack-Core", "Pack-Raids"], Directory.GetDirectories(code).Select(Path.GetFileName).Order());

        // A repo that is one addon is named after its .toc, not the repo
        var single = Path.Combine(_root, "single");
        Assert.Equal(["Questie"], AddonFiles.ExtractAddons(Zip("single.zip",
            ("Owner-Questie-335-9f8e7d/Questie.toc", ""), ("Owner-Questie-335-9f8e7d/Questie-Classic.toc", "")), single));
        Assert.True(File.Exists(Path.Combine(single, @"Questie\Questie-Classic.toc")));

        // A release zip has the addon folders at the top
        Assert.Equal(["WeakAuras", "WeakAurasOptions"], AddonFiles.ExtractAddons(Zip("release.zip",
            ("WeakAuras/WeakAuras.toc", ""), ("WeakAurasOptions/WeakAurasOptions.toc", "")), Path.Combine(_root, "release")).Order());

        // An addon zipped without its folder, with a path that tries to climb out
        var bare = Path.Combine(_root, "bare");
        Assert.Equal(["Bare"], AddonFiles.ExtractAddons(Zip("bare.zip", ("Bare.toc", ""), ("Core.lua", ""), ("../../escaped.lua", "")), bare));
        Assert.Equal(["Bare.toc", "Core.lua"], Directory.GetFiles(Path.Combine(bare, "Bare")).Select(Path.GetFileName).Order());
        Assert.False(File.Exists(Path.Combine(_root, "escaped.lua")));
    }

    private string Zip(string name, params (string Entry, string Text)[] files)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }
}
