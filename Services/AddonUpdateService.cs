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

    /// <summary>The repos to check: the ones the user listed, or until then the ones the addons' .toc files name.</summary>
    public static List<string> Sources(GameEntry game) =>
        game.AddonSources ?? AddonFiles.GitHubRepos(AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game)));

    /// <summary>Downloads every source. Its installed addons that differ are its update; what isn't installed is left out.</summary>
    public async Task<List<AddonUpdate>> CheckAsync(GameEntry game, IProgress<string>? progress, CancellationToken ct)
    {
        var sources = Sources(game);
        var gameAddOns = AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game));
        var clientMajor = AddonFiles.ClientMajorVersion(game.ExecutablePath);
        var downloadDir = Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));
        var updates = new List<AddonUpdate>();
        for (var i = 0; i < sources.Count; i++)
        {
            var repo = sources[i];
            progress?.Report($"Downloading {repo} ({i + 1} of {sources.Count})...");
            var dir = Path.Combine(downloadDir, i.ToString());
            try
            {
                Directory.CreateDirectory(downloadDir);
                var release = await gitHub.DownloadLatestAsync(repo, dir + ".zip", ct);
                var preview = await Task.Run(() =>
                {
                    AddonFiles.ExtractAddons(dir + ".zip", dir);
                    return AddonFiles.Compare(dir, gameAddOns, includeRemoved: false, clientMajor, ct);
                }, ct);
                updates.Add(new AddonUpdate(repo, release, dir,
                    preview.Diffs.Where(d => d.Change == AddonChange.Changed).ToList(), preview.Unchanged, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                updates.Add(new AddonUpdate(repo, null, dir, [], 0, ex.Message));
            }
        }
        return updates;
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
