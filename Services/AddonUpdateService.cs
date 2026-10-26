using System;
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
public class AddonUpdateService
{
    private static readonly string DownloadRoot = Path.Combine(Path.GetTempPath(), "iscLauncher-addon-updates");

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

    /// <summary>A folder for one list's downloads, for <see cref="Discard"/> once the list closes.</summary>
    public static string NewDownloadDir() => Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));

    /// <summary>Downloads a repo into <paramref name="downloadDir"/> and compares it with the game.</summary>
    public async Task<AddonUpdate> CheckAsync(GameEntry game, string repo, string downloadDir, CancellationToken ct)
    {
        var dir = Path.Combine(downloadDir, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(downloadDir);
            var release = await _gitHub.DownloadLatestAsync(repo, dir + ".zip", ct);
            return await Task.Run(() =>
            {
                AddonFiles.ExtractAddons(dir + ".zip", dir);
                return Compare(game, new AddonUpdate(repo, release, dir, [], [], null));
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AddonUpdate(repo, null, dir, [], [], ex.Message);
        }
    }

    /// <summary>
    /// Compares a download with what the game has now, so a pull or an update shows without downloading again. Only
    /// addons the game has are its update; the rest of the download is left out.
    /// </summary>
    public static AddonUpdate Compare(GameEntry game, AddonUpdate update)
    {
        if (update.Error != null)
            return update;
        var gameAddOns = AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game));
        var downloaded = AddonFiles.AddonNames(update.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preview = AddonFiles.Compare(update.Dir, gameAddOns, includeRemoved: false, AddonFiles.ClientMajorVersion(game.ExecutablePath), CancellationToken.None);
        return update with
        {
            Installed = AddonFiles.AddonNames(gameAddOns).Where(downloaded.Contains).ToList(),
            Changed = preview.Diffs.Where(d => d.Change == AddonChange.Changed).ToList()
        };
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
}
