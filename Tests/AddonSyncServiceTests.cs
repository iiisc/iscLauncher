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
