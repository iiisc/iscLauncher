using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

/// <summary>
/// Checks the GitHub repos a game's addons come from by downloading each and comparing it with what's installed.
/// Versions in .toc files can't be trusted for this, and some addons publish only code, never releases.
/// </summary>
public class AddonUpdateService(GitHubService gitHub)
{
    private static readonly string DownloadRoot = Path.Combine(Path.GetTempPath(), "iscLauncher-addon-updates");

    /// <summary>The repos the user connected, or until they change them, the ones the addons' .toc files name.</summary>
    public static List<AddonSource> Sources(GameEntry game) =>
        game.AddonSources ?? AddonFiles.TocGitHubSources(AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game)));

    /// <summary>Downloads a repo and compares it with the game. Only addons the game has are its update; the rest are left out.</summary>
    public async Task<AddonUpdate> CheckAsync(GameEntry game, string repo, CancellationToken ct)
    {
        var gameAddOns = AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game));
        var dir = Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(DownloadRoot);
            var release = await gitHub.DownloadLatestAsync(repo, dir + ".zip", ct);
            return await Task.Run(() =>
            {
                var downloaded = AddonFiles.ExtractAddons(dir + ".zip", dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var installed = AddonFiles.AddonNames(gameAddOns).Where(downloaded.Contains).ToList();
                var preview = AddonFiles.Compare(dir, gameAddOns, includeRemoved: false, AddonFiles.ClientMajorVersion(game.ExecutablePath), ct);
                return new AddonUpdate(repo, release, dir, installed, preview.Diffs.Where(d => d.Change == AddonChange.Changed).ToList(), null);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AddonUpdate(repo, null, dir, [], [], ex.Message);
        }
    }

    /// <summary>Deletes what checks downloaded. Housekeeping, so a file still in use is left for next time.</summary>
    public static void DiscardDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadRoot))
                AddonFiles.ForceDeleteDirectory(DownloadRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
