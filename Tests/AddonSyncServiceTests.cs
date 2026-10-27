using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Services;
using Xunit;
using static iscLauncher.Tests.Sandbox;

namespace iscLauncher.Tests;

public sealed class AddonSyncServiceTests : System.IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly CancellationToken _ct = CancellationToken.None;
    private AddonSyncService Sync => _sandbox.Sync;

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task TwoPcsSyncThroughTheRepoAndCanRollBack()
    {
        var remote = _sandbox.NewRemote();
        var pcA = _sandbox.AddGame("pcA", remote);
        var pcB = _sandbox.AddGame("pcB", remote);
        var dirA = Dir(pcA);
        var dirB = Dir(pcB);
        Addon(dirA, "Questie", "7.1");
        Addon(dirA, "Details", "1.0");
        Addon(dirA, "Blizzard_Thing", "1");
        Write(Path.Combine(dirA, @"WTF\Account\ACC\SavedVariables\Questie.lua"), "QuestieDB = 1");
        Write(Path.Combine(dirA, @"WTF\Account\ACC\SavedVariables\Questie.lua.bak"), "old");
        Write(Path.Combine(dirA, @"WTF\Account\ACC\Realm\Hero\layout-local.txt"), "layout");

        // A pushes to the empty repo; Blizzard_ folders ship with the client and stay out of it
        var session = await Sync.OpenAsync(pcA, null, _ct);
        Assert.Null(session.HeadCommit);
        Assert.Null(await Sync.DescribeUnpulledChangesAsync(session, _ct));
        var preview = await Sync.PreviewPushAsync(session, _ct);
        Assert.Equal(1, preview.Characters);
        Assert.Equal(["Details", "Questie"], preview.Diffs.Select(d => d.Name).Order());
        Assert.All(preview.Diffs, d => Assert.Equal(AddonChange.New, d.Change));
        await Sync.PushAsync(session, preview.Diffs, new SyncOptions(true, true), null, _ct);

        var firstPush = Git(remote, "rev-parse", "main");
        Assert.Equal(firstPush, pcA.LastSyncedCommit);
        // What the push left out (the Blizzard_ folder, the .bak file) mustn't make closing the game offer another push
        Assert.False(await Sync.HasLocalChangesAsync(pcA, _ct));
        var reloaded = new GameRepository(Path.Combine(_sandbox.Root, "games.json"));
        await reloaded.LoadAsync();
        Assert.Equal(firstPush, reloaded.Games.Single(g => g.Id == pcA.Id).LastSyncedCommit);
        var tree = Git(remote, "ls-tree", "-r", "--name-only", "main");
        Assert.Contains("AddOns/Questie/Questie.toc", tree);
        Assert.Contains("WTF/Account/ACC/Realm/Hero/layout-local.txt", tree);
        Assert.DoesNotContain(".bak", tree);

        // B pulls addon settings only; its local-only addon is offered for removal and backed up
        Addon(dirB, "LocalOnly", "0.1");
        Write(Path.Combine(dirB, @"WTF\Account\ACC\Realm\Hero\layout-local.txt"), "B layout");
        session = await Sync.OpenAsync(pcB, null, _ct);
        preview = await Sync.PreviewPullAsync(session, _ct);
        Assert.Equal("LocalOnly", preview.Diffs.Single(d => d.Change == AddonChange.Removed).Name);
        Assert.All(preview.Diffs.Where(d => d.Change == AddonChange.New), d => Assert.Contains("this game is 2.x", d.Warning));
        await Sync.PullAsync(session, preview.Diffs, new SyncOptions(true, false), null, _ct);

        Assert.True(Directory.Exists(Path.Combine(dirB, @"Interface\AddOns\Questie")));
        Assert.False(Directory.Exists(Path.Combine(dirB, @"Interface\AddOns\LocalOnly")));
        Assert.True(File.Exists(Path.Combine(dirB, @"WTF\Account\ACC\SavedVariables\Questie.lua")));
        Assert.Equal("B layout", File.ReadAllText(Path.Combine(dirB, @"WTF\Account\ACC\Realm\Hero\layout-local.txt")));
        using (var zip = ZipFile.OpenRead(Sync.ListBackups(pcB).Single().Path))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "AddOns/LocalOnly/LocalOnly.toc");
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("WTF/Account/"));
        }

        // A updates one addon and drops another; B is told it's behind before it pushes
        Addon(dirA, "Questie", "7.3", body: "new code");
        Directory.Delete(Path.Combine(dirA, @"Interface\AddOns\Details"), true);
        Assert.True(await Sync.HasLocalChangesAsync(pcA, _ct));
        session = await Sync.OpenAsync(pcA, null, _ct);
        preview = await Sync.PreviewPushAsync(session, _ct);
        var questie = preview.Diffs.Single(d => d.Name == "Questie");
        Assert.Equal((AddonChange.Changed, "7.3", "7.1"), (questie.Change, questie.SourceVersion, questie.TargetVersion));
        Assert.Equal(AddonChange.Removed, preview.Diffs.Single(d => d.Name == "Details").Change);
        await Sync.PushAsync(session, preview.Diffs, new SyncOptions(false, false), null, _ct);

        session = await Sync.OpenAsync(pcB, null, _ct);
        Assert.Contains("hasn't pulled", await Sync.DescribeUnpulledChangesAsync(session, _ct));
        preview = await Sync.PreviewPullAsync(session, _ct);
        Assert.Equal(2, preview.Diffs.Count);
        await Sync.PullAsync(session, preview.Diffs, new SyncOptions(false, false), null, _ct);
        session = await Sync.OpenAsync(pcB, null, _ct);
        preview = await Sync.PreviewPullAsync(session, _ct);
        Assert.Empty(preview.Diffs);
        Assert.Equal(1, preview.Unchanged);
        Assert.Contains("new code", File.ReadAllText(Path.Combine(dirB, @"Interface\AddOns\Questie\Questie.lua")));

        // Rolling back to the first push is a new commit whose tree matches it exactly
        var first = (await Sync.GetCommitLogAsync(session, _ct)).Last();
        preview = await Sync.PreviewRollbackAsync(session, first.Hash, _ct);
        Assert.Contains(preview.Diffs, d => d.Name == "Details" && d.Change == AddonChange.New);
        Assert.Contains(preview.Diffs, d => d.Name == "Questie" && d.Change == AddonChange.Changed);
        await Sync.RollbackAsync(session, first, preview.Diffs, new SyncOptions(false, false), null, _ct);
        Assert.Equal(Git(remote, "rev-parse", firstPush + "^{tree}"), Git(remote, "rev-parse", "main^{tree}"));
        Assert.Equal("3", Git(remote, "rev-list", "--count", "main"));
    }

    [Fact]
    public async Task SettingsTheRepoDoesntHoldDontMakeClosingTheGameOfferAPush()
    {
        var remote = _sandbox.NewRemote();
        var game = _sandbox.AddGame("pc", remote);
        Addon(Dir(game), "Questie", "7.1");
        var addonSettings = Path.Combine(Dir(game), @"WTF\Account\ACC\SavedVariables\Questie.lua");
        var characterSettings = Path.Combine(Dir(game), @"WTF\Account\ACC\Realm\Hero\layout-local.txt");
        Write(addonSettings, "QuestieDB = 1");
        Write(characterSettings, "layout");

        // The push dialog's defaults: addon settings, but not character settings
        var session = await Sync.OpenAsync(game, null, _ct);
        await Sync.PushAsync(session, (await Sync.PreviewPushAsync(session, _ct)).Diffs, new SyncOptions(true, false), null, _ct);
        Write(characterSettings, "moved a frame");
        Assert.False(await Sync.HasLocalChangesAsync(game, _ct));

        Write(addonSettings, "QuestieDB = 2");
        Assert.True(await Sync.HasLocalChangesAsync(game, _ct));
    }

    [Fact]
    public async Task PullingFromAnEmptyRepoIsRefused()
    {
        var remote = _sandbox.NewRemote();
        var game = _sandbox.AddGame("pc", remote);
        Addon(Dir(game), "Questie", "7.1");

        // Every installed addon would show as not in the repo, ticked for removal
        var session = await Sync.OpenAsync(game, null, _ct);
        var error = await Assert.ThrowsAsync<AppException>(() => Sync.PreviewPullAsync(session, _ct));
        Assert.Contains("empty", error.Message);
        Assert.True(Directory.Exists(Path.Combine(Dir(game), @"Interface\AddOns\Questie")));
    }

    [Fact]
    public async Task PushingNothingLeavesAnotherPcsPushUnpulled()
    {
        var remote = _sandbox.NewRemote();
        var pcA = _sandbox.AddGame("pcA", remote);
        var pcB = _sandbox.AddGame("pcB", remote);
        Addon(Dir(pcA), "Questie", "7.1");
        var session = await Sync.OpenAsync(pcA, null, _ct);
        await Sync.PushAsync(session, (await Sync.PreviewPushAsync(session, _ct)).Diffs, new SyncOptions(false, false), null, _ct);
        var firstPush = pcA.LastSyncedCommit;

        Addon(Dir(pcB), "Questie", "7.1");
        Addon(Dir(pcB), "Details", "1.0");
        session = await Sync.OpenAsync(pcB, null, _ct);
        await Sync.PushAsync(session, (await Sync.PreviewPushAsync(session, _ct)).Diffs, new SyncOptions(false, false), null, _ct);

        // A unticks everything B changed, so nothing is pushed and A still hasn't got B's push
        session = await Sync.OpenAsync(pcA, null, _ct);
        Assert.Equal("Nothing to push — the repo already matches.", await Sync.PushAsync(session, [], new SyncOptions(false, false), null, _ct));
        Assert.Equal(firstPush, pcA.LastSyncedCommit);
        Assert.True(pcA.HasRemoteChanges);
    }

    [Fact]
    public async Task OpeningResetsALeftoverCacheToTheRemote()
    {
        var remote = _sandbox.NewRemote();
        var game = _sandbox.AddGame("pc", remote);
        Addon(Dir(game), "Questie", "7.1");
        var session = await Sync.OpenAsync(game, null, _ct);
        await Sync.PushAsync(session, (await Sync.PreviewPushAsync(session, _ct)).Diffs, new SyncOptions(false, false), null, _ct);

        // What a failed or cancelled operation can leave behind: stray files and a commit that never got pushed
        var cache = session.CachePath;
        Write(Path.Combine(cache, "junk.txt"), "junk");
        Write(Path.Combine(cache, @"AddOns\Questie\Questie.lua"), "local edit");
        Git(cache, "-c", "user.name=t", "-c", "user.email=t", "commit", "-qam", "unpushed");

        session = await Sync.OpenAsync(game, null, _ct);
        Assert.Equal(Git(remote, "rev-parse", "main"), session.HeadCommit);
        Assert.False(File.Exists(Path.Combine(cache, "junk.txt")));
        Assert.Equal("", Git(cache, "status", "--porcelain"));
    }

    [Fact]
    public async Task AFileOpenElsewhereStopsTheCopyBeforeAnythingChanges()
    {
        var source = _sandbox.AddGame("source");
        var target = _sandbox.AddGame("target");
        Addon(Dir(source), "Questie", "7.3");
        Addon(Dir(target), "Questie", "6.0");
        var targetLua = Path.Combine(Dir(target), @"Interface\AddOns\Questie\Questie.lua");

        var preview = await Sync.PreviewCopyAsync(source, target, _ct);
        using (new FileStream(targetLua, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<AppException>(() => Sync.CopyToGameAsync(source, target, preview.Diffs, copySettings: false, _ct));
            Assert.Contains(@"Questie\Questie.lua is open in another program", error.Message);
        }

        Assert.Contains("6.0", File.ReadAllText(targetLua));
        Assert.Empty(Sync.ListBackups(target));
    }

    [Fact]
    public async Task AFailureHalfwayKeepsEveryBackupAndSaysHowToRecover()
    {
        var source = _sandbox.AddGame("source");
        var target = _sandbox.AddGame("target");
        Write(Path.Combine(Dir(target), @"WTF\Account\ACC\SavedVariables\Questie.lua"), "settings");
        Addon(Dir(target), "Questie", "6.0");

        // Four successful copies leave the newest three backups
        for (var i = 1; i <= 4; i++)
        {
            Addon(Dir(source), "Questie", $"7.{i}");
            var diffs = (await Sync.PreviewCopyAsync(source, target, _ct)).Diffs;
            await Sync.CopyToGameAsync(source, target, diffs, copySettings: true, _ct);
        }
        Assert.Equal(3, Sync.ListBackups(target).Count);

        // A source file that can't be read fails the copy after the target's copy of the addon was already removed
        Addon(Dir(source), "Questie", "8.0");
        var preview = await Sync.PreviewCopyAsync(source, target, _ct);
        using (new FileStream(Path.Combine(Dir(source), @"Interface\AddOns\Questie\Questie.lua"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<AppException>(() => Sync.CopyToGameAsync(source, target, preview.Diffs, copySettings: true, _ct));
            Assert.Contains("Stopped partway", error.Message);
            Assert.Contains("Restore a Backup", error.Message);
            Assert.NotNull(error.InnerException);
        }

        // The backup from just before the failure holds what the game had, and nothing older was pruned for it
        var backups = Sync.ListBackups(target);
        Assert.Equal(4, backups.Count);
        await Sync.RestoreBackupAsync(target, backups[0], _ct);
        Assert.Contains("7.4", File.ReadAllText(Path.Combine(Dir(target), @"Interface\AddOns\Questie\Questie.lua")));
    }

    [Fact]
    public async Task InstallingAnUpdateReplacesOnlyTheChangedAddonsAndBacksThemUp()
    {
        var game = _sandbox.AddGame("game");
        Addon(Dir(game), "WeakAuras", "5.21.8");
        Addon(Dir(game), "WeakAurasOptions", "5.21.8");
        Write(Path.Combine(Dir(game), @"Interface\AddOns\WeakAuras\Dropped.lua"), "gone in the new version");
        var download = Path.Combine(_sandbox.Root, "download");
        Addon(download, "WeakAuras", "5.22.1");
        Addon(download, "WeakAurasOptions", "5.21.8");
        Addon(download, "WeakAurasArchive", "5.22.1");

        var update = AddonUpdateService.Compare(game,
            new AddonUpdate("NoM0Re/WeakAuras-WotLK", "5.22.1", "5.22.1", AddonFiles.GameAddOnsDir(download), [], [], null));
        Assert.Equal(["WeakAuras", "WeakAurasOptions"], update.Installed);
        var changed = Assert.Single(update.Changed);
        Assert.Equal(("WeakAuras", "5.21.8", "5.22.1"), (changed.Name, changed.TargetVersion, changed.SourceVersion));

        Assert.Equal("Updated NoM0Re/WeakAuras-WotLK.", await Sync.InstallUpdatesAsync(game, [update], _ct));
        // Compared again without downloading, the game now matches, and its files have a new fingerprint to match on
        var after = AddonUpdateService.Compare(game, update);
        Assert.Empty(after.Changed);
        Assert.NotEqual(update.Files, after.Files);

        var addOns = AddonFiles.GameAddOnsDir(Dir(game));
        Assert.Contains("5.22.1", File.ReadAllText(Path.Combine(addOns, @"WeakAuras\WeakAuras.lua")));
        Assert.False(File.Exists(Path.Combine(addOns, @"WeakAuras\Dropped.lua")));
        // Parts of the download that weren't installed stay out
        Assert.False(Directory.Exists(Path.Combine(addOns, "WeakAurasArchive")));
        Assert.Equal(["WeakAuras"], Assert.Single(Sync.ListBackups(game)).Addons);
    }

    [Fact]
    public async Task AnUpdateBringsTheNewFoldersItsAddonsNeed()
    {
        var game = _sandbox.AddGame("game");
        Addon(Dir(game), "WeakAuras", "5.21.8");
        var download = Path.Combine(_sandbox.Root, "download");
        var downloadAddOns = AddonFiles.GameAddOnsDir(download);
        Addon(download, "WeakAuras", "5.22.1");
        Write(Path.Combine(downloadAddOns, @"WeakAuras\WeakAuras.toc"), "## Interface: 30300\n## Version: 5.22.1\n## Dependencies: WeakAurasCore\nWeakAuras.lua\n");
        Addon(download, "WeakAurasCore", "5.22.1");
        Write(Path.Combine(downloadAddOns, @"WeakAurasCore\WeakAurasCore.toc"), "## Interface: 30300\n## RequiredDeps: LibCompress\n## OptionalDeps: WeakAurasArchive\n");
        Addon(download, "LibCompress", "1.0");
        Addon(download, "WeakAurasArchive", "5.22.1");

        var update = AddonUpdateService.Compare(game, new AddonUpdate("NoM0Re/WeakAuras-WotLK", "5.22.1", "5.22.1", downloadAddOns, [], [], null));
        Assert.Equal(["LibCompress", "WeakAuras", "WeakAurasCore"], update.Changed.Select(d => d.Name).Order());

        await Sync.InstallUpdatesAsync(game, [update], _ct);
        var addOns = AddonFiles.GameAddOnsDir(Dir(game));
        Assert.True(Directory.Exists(Path.Combine(addOns, "WeakAurasCore")));
        Assert.True(Directory.Exists(Path.Combine(addOns, "LibCompress")));
        Assert.False(Directory.Exists(Path.Combine(addOns, "WeakAurasArchive")));
        Assert.Empty(AddonUpdateService.Compare(game, update).Changed);
    }

    [Fact]
    public async Task CopyingToAnotherGameReplacesAddonsAndCopiesTheirSettings()
    {
        var source = _sandbox.AddGame("source");
        var target = _sandbox.AddGame("target");
        Addon(Dir(source), "Questie", "7.3", body: "new code");
        Addon(Dir(target), "Questie", "6.0");
        Addon(Dir(target), "TargetOnly", "1.0");
        Write(Path.Combine(Dir(source), @"WTF\Account\ACC\SavedVariables\Questie.lua"), "QuestieDB = 1");

        var preview = await Sync.PreviewCopyAsync(source, target, _ct);
        var questie = Assert.Single(preview.Diffs);
        Assert.Equal(("Questie", AddonChange.Changed), (questie.Name, questie.Change));
        await Sync.CopyToGameAsync(source, target, preview.Diffs, copySettings: true, _ct);

        Assert.Contains("new code", File.ReadAllText(Path.Combine(Dir(target), @"Interface\AddOns\Questie\Questie.lua")));
        Assert.True(Directory.Exists(Path.Combine(Dir(target), @"Interface\AddOns\TargetOnly")));
        Assert.Equal("QuestieDB = 1", File.ReadAllText(Path.Combine(Dir(target), @"WTF\Account\ACC\SavedVariables\Questie.lua")));
    }
}
