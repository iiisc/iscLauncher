using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

/// <summary>
/// Checks the GitHub repos a game's addons come from. When a repo's version or the installed files have changed since
/// they last matched, the repo is downloaded and compared with what's installed: versions in .toc files can't be
/// trusted for this, and some addons publish only code, never releases.
/// </summary>
public class AddonUpdateService
{
    private static readonly string DownloadRoot = Path.Combine(Path.GetTempPath(), "iscLauncher-addon-updates");
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    private readonly GitHubService _gitHub;

    public AddonUpdateService(GitHubService gitHub)
    {
        _gitHub = gitHub;
        // Each list deletes its downloads when it closes; these are from a run that ended without closing it
        if (Directory.Exists(DownloadRoot))
        {
            var leftovers = Directory.GetDirectories(DownloadRoot);
            Task.Run(() => Array.ForEach(leftovers, Discard));
        }
    }

    /// <summary>
    /// The repos a game's addons are updated from: the ones already known, plus any an installed addon's .toc names,
    /// unless the user disconnected it.
    /// </summary>
    public static List<AddonSource> Sources(GameEntry game, IReadOnlyDictionary<string, (string? Version, string? Repo)> installed)
    {
        var sources = new List<AddonSource>(game.AddonSources ?? []);
        var disconnected = game.DisconnectedAddonSources ?? [];
        foreach (var named in installed.Where(a => a.Value.Repo != null).GroupBy(a => a.Value.Repo!, IgnoreCase))
        {
            if (!sources.Any(s => IgnoreCase.Equals(s.Repo, named.Key)) && !disconnected.Contains(named.Key, IgnoreCase))
                sources.Add(new AddonSource { Repo = named.Key, Addons = named.Select(a => a.Key).ToList() });
        }
        return sources;
    }

    /// <summary>A folder for one list's downloads, for <see cref="Discard"/> once the list closes.</summary>
    public static string NewDownloadDir() => Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));

    /// <summary>Checks a repo, downloading it into <paramref name="downloadDir"/> only if something changed since its addons last matched.</summary>
    public async Task<AddonUpdate> CheckAsync(GameEntry game, AddonSource source, string downloadDir, CancellationToken ct)
    {
        var dir = Path.Combine(downloadDir, Guid.NewGuid().ToString("N"));
        try
        {
            var latest = await _gitHub.GetLatestAsync(source.Repo, ct);
            var gameAddOns = GameAddOns(game);
            var (installed, files) = await Task.Run(() =>
            {
                var present = source.Addons.Where(a => Directory.Exists(Path.Combine(gameAddOns, a))).ToList();
                return (present, AddonFiles.Fingerprint(gameAddOns, present));
            }, ct);
            if (installed.Count > 0 && latest.Id == source.MatchedVersion && files == source.MatchedFiles)
                return new AddonUpdate(source.Repo, latest.Id, latest.Release, null, installed, [], null, files);

            Directory.CreateDirectory(downloadDir);
            await _gitHub.DownloadAsync(latest, dir + ".zip", ct);
            return await Task.Run(() =>
            {
                AddonFiles.ExtractAddons(dir + ".zip", dir);
                return Compare(game, new AddonUpdate(source.Repo, latest.Id, latest.Release, dir, [], [], null));
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AddonUpdate(source.Repo, null, null, null, [], [], ex.Message);
        }
    }

    /// <summary>
    /// Compares a download with what the game has now, so a pull or an update shows without downloading again. Only
    /// addons the game has, and new ones they need, are its update; the rest of the download is left out.
    /// </summary>
    public static AddonUpdate Compare(GameEntry game, AddonUpdate update)
    {
        if (update.Error != null || update.Dir == null)
            return update;
        var gameAddOns = GameAddOns(game);
        var downloaded = AddonFiles.AddonNames(update.Dir).ToHashSet(IgnoreCase);
        var installed = AddonFiles.AddonNames(gameAddOns).Where(downloaded.Contains).ToList();
        var preview = AddonFiles.Compare(update.Dir, gameAddOns, includeRemoved: false, AddonFiles.ClientMajorVersion(game.ExecutablePath), CancellationToken.None);
        var needed = AddonFiles.Dependencies(update.Dir, installed);
        return update with
        {
            Installed = installed,
            Changed = preview.Diffs.Where(d => d.Change == AddonChange.Changed || needed.Contains(d.Name)).ToList(),
            Files = AddonFiles.Fingerprint(gameAddOns, installed)
        };
    }

    /// <summary>
    /// Marks the game when one of its repos has a version its addons haven't matched, without downloading anything.
    /// A repo that was never checked counts, so the game is worth a look.
    /// </summary>
    public async Task RefreshAsync(GameEntry game, CancellationToken ct)
    {
        var sources = await Task.Run(() => Sources(game, AddonFiles.InstalledAddons(GameAddOns(game))), ct);
        foreach (var source in sources)
        {
            if ((await _gitHub.GetLatestAsync(source.Repo, ct)).Id != source.MatchedVersion)
            {
                game.HasAddonUpdates = true;
                return;
            }
        }
        game.HasAddonUpdates = false;
    }

    /// <summary>Repos that may hold an addon: searched with the game's version first, since forks for other clients abound.</summary>
    public async Task<List<(string Repo, string Description, int Stars)>> FindReposAsync(string addon, GameEntry game, CancellationToken ct)
    {
        var found = AddonFiles.ClientVersion(game.ExecutablePath) is { } version
            ? await _gitHub.SearchReposAsync($"{addon} {version}", ct)
            : [];
        return found.Count > 0 ? found : await _gitHub.SearchReposAsync(addon, ct);
    }

    /// <summary>Deletes downloads. Housekeeping, so a file still in use is left behind.</summary>
    public static void Discard(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                AddonFiles.ForceDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string GameAddOns(GameEntry game) => AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game));
}
