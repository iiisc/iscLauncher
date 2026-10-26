using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;

namespace iscLauncher.Services;

public record CharacterFolder(GameEntry Game, string Dir, string Label);

public record BackupInfo(string Path, DateTime Created, List<string> Addons, bool HasSettings);

/// <summary>A GitHub repo checked for addon updates.</summary>
/// <param name="Latest">Its newest version: a release tag or a commit.</param>
/// <param name="Release">The release's tag, or null when the newest version is a commit.</param>
/// <param name="Dir">Where its addons were unpacked, or null if it wasn't downloaded because nothing had changed.</param>
/// <param name="Installed">The game's addon folders that the repo has.</param>
/// <param name="Changed">Those of them that differ from the download, and folders new in it that they need.</param>
/// <param name="Error">Why it couldn't be checked.</param>
/// <param name="Files">A fingerprint of the installed folders when they were compared.</param>
public record AddonUpdate(string Repo, string? Latest, string? Release, string? Dir, List<string> Installed, List<AddonDiff> Changed,
    string? Error, string? Files = null);

/// <summary>A game's sync repo, checked out as an exact copy of its remote branch.</summary>
public record SyncSession(GameEntry Game, string CachePath, string? HeadCommit)
{
    public string GameDir => AddonSyncService.GameDir(Game);
    public string GameAddOns => AddonFiles.GameAddOnsDir(GameDir);
    public string RepoAddOns => Path.Combine(CachePath, "AddOns");
    public string GameWtfAccount => AddonFiles.WtfAccountDir(GameDir);
    public string RepoWtfAccount => AddonFiles.WtfAccountDir(CachePath);
}

/// <summary>
/// Moves addons and WTF settings between a game folder and a git repo, or between two games. Repo operations open a
/// session (fetch + reset to the remote), preview what would change, then apply only what the user picked.
/// Failures throw <see cref="AppException"/>.
/// </summary>
public class AddonSyncService
{

    private static readonly string BackupRoot = Path.Combine(GameRepository.AppDataFolder, "Backups");

    private readonly GitService _git;
    private readonly GameRepository _gameRepository;
    private const string SyncBranch = "main";

    public AddonSyncService(GitService git, GameRepository gameRepository)
    {
        _git = git;
        _gameRepository = gameRepository;
    }

    public async Task<SyncSession> OpenAsync(GameEntry game, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Checking prerequisites...");
        if (!await _git.IsGitInstalledAsync(ct))
            throw new AppException("Git wasn't found. The release download includes it; otherwise install Git from git-scm.com.");
        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            throw new AppException("No sync repo URL configured.");
        if (!File.Exists(game.ExecutablePath))
            throw new AppException("Game executable not found.");

        progress?.Report("Fetching from repo...");
        var cachePath = _git.GetLocalCachePath(game);
        var mirror = await _git.MirrorAsync(game.SyncRepoUrl.Trim(), SyncBranch, cachePath, progress, ct);
        if (!mirror.Success)
            throw new AppException("Couldn't fetch from the repo. Check the URL, that you're signed in to GitHub under Options, and that the repo " +
                $"is one of the repos you've given iscLauncher (Options → Choose which repos it can use). {mirror.Output.Trim()}");

        var head = await _git.GetHeadAsync(cachePath, ct);
        game.RemoteHead = head;
        return new SyncSession(game, cachePath, head);
    }

    public Task<AddonPreview> PreviewPullAsync(SyncSession s, CancellationToken ct)
    {
        // Every addon on this PC would show as not in the repo, ticked for removal
        if (s.HeadCommit == null)
            throw new AppException("The repo is empty, so there's nothing to pull. Push from a PC that has this game's addons first.");
        return Task.Run(() => AddonFiles.Compare(s.RepoAddOns, s.GameAddOns, includeRemoved: true,
            AddonFiles.ClientMajorVersion(s.Game.ExecutablePath), ct) with { Characters = AddonFiles.CharacterCount(s.CachePath) }, ct);
    }

    public Task<AddonPreview> PreviewPushAsync(SyncSession s, CancellationToken ct) =>
        Task.Run(() => AddonFiles.Compare(s.GameAddOns, s.RepoAddOns, includeRemoved: true, clientMajor: 0, ct)
            with { Characters = AddonFiles.CharacterCount(s.GameDir) }, ct);

    /// <summary>Puts the cache's files back to <paramref name="commit"/> and previews applying them. The next session discards this unless <see cref="RollbackAsync"/> commits it.</summary>
    public async Task<AddonPreview> PreviewRollbackAsync(SyncSession s, string commit, CancellationToken ct)
    {
        var result = await _git.RestoreTreeAsync(s.CachePath, commit, ct);
        if (!result.Success)
            throw new AppException($"Couldn't load that version: {result.Output.Trim()}");
        return await PreviewPullAsync(s, ct);
    }

    /// <summary>The repo's newest commit if this PC hasn't pulled or pushed it yet, or null if it's caught up.</summary>
    public async Task<GitCommitEntry?> FindUnpulledChangeAsync(SyncSession s, CancellationToken ct) =>
        s.Game.HasRemoteChanges ? (await _git.LogAsync(s.CachePath, 1, ct)).FirstOrDefault() : null;

    public async Task<string?> DescribeUnpulledChangesAsync(SyncSession s, CancellationToken ct) =>
        await FindUnpulledChangeAsync(s, ct) is { } latest
            ? $"⚠ The repo has changes this PC hasn't pulled. Latest: \"{latest.Message}\" ({latest.DisplayDate}). " +
              "Pushing replaces them for everything you select. Pull first to keep them."
            : null;

    public async Task<string> PullAsync(SyncSession s, IReadOnlyList<AddonDiff> addons, SyncOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var settings = options.AddonSettings || options.CharacterSettings;
        await Task.Run(() => ChangeGameFiles(s.Game, addons.Select(a => a.Name), settings, progress, () =>
        {
            AddonFiles.ApplyAddons(s.RepoAddOns, s.GameAddOns, addons, CancellationToken.None);
            AddonFiles.CopyWtf(s.RepoWtfAccount, s.GameWtfAccount, options, mirror: false, CancellationToken.None);
        }, ct), ct);

        await MarkSyncedAsync(s.Game, s.HeadCommit);
        return $"Pulled. {Describe(addons, options)}";
    }

    public async Task<string> PushAsync(SyncSession s, IReadOnlyList<AddonDiff> addons, SyncOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Copying to repo...");
        await Task.Run(() =>
        {
            AddonFiles.ApplyAddons(s.GameAddOns, s.RepoAddOns, addons, ct);
            AddonFiles.CopyWtf(s.GameWtfAccount, s.RepoWtfAccount, options, mirror: true, ct);
        }, ct);

        var gitignorePath = Path.Combine(s.CachePath, ".gitignore");
        if (!File.Exists(gitignorePath))
            await File.WriteAllTextAsync(gitignorePath, "*.old\n*.bak\nThumbs.db\n.DS_Store\n", ct);

        var repoAddons = AddonFiles.AddonNames(s.RepoAddOns);
        var characters = AddonFiles.CharacterCount(s.CachePath);
        var pcName = Environment.MachineName;
        var subject = $"sync: {s.Game.Name} — {repoAddons.Count.Plural("addon")}, {characters.Plural("character")} [{pcName}]";
        var message = string.Join("\n", new[] { subject, "", "Addons:" }.Concat(repoAddons.Select(n => $"  - {n}")));

        var head = await CommitAndPushAsync(s, message, progress, ct);
        // The repo's head may be another PC's push that the user left unticked, which this PC still hasn't got
        if (head == s.HeadCommit)
            return "Nothing to push — the repo already matches.";
        await MarkSyncedAsync(s.Game, head);
        return $"Pushed. {Describe(addons, options)}";
    }

    public async Task<List<GitCommitEntry>> GetCommitLogAsync(SyncSession s, CancellationToken ct) =>
        await _git.LogAsync(s.CachePath, 20, ct);

    /// <summary>Commits the files restored by <see cref="PreviewRollbackAsync"/> for everyone, then applies the chosen parts to this PC.</summary>
    public async Task<string> RollbackAsync(SyncSession s, GitCommitEntry target, IReadOnlyList<AddonDiff> addons, SyncOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var pcName = Environment.MachineName;
        var subject = $"rollback: {s.Game.Name} — restore to {target.Hash}: {target.Message} [{pcName}]";
        var message = string.IsNullOrWhiteSpace(target.Body) ? subject : $"{subject}\n\n{target.Body}";

        var head = await CommitAndPushAsync(s, message, progress, ct);
        await PullAsync(s with { HeadCommit = head }, addons, options, progress, ct);
        return $"Rolled back to {target.Hash}. {Describe(addons, options)}";
    }

    public Task<AddonPreview> PreviewCopyAsync(GameEntry source, GameEntry target, CancellationToken ct) =>
        Task.Run(() => AddonFiles.Compare(AddonFiles.GameAddOnsDir(GameDir(source)), AddonFiles.GameAddOnsDir(GameDir(target)),
            includeRemoved: false, AddonFiles.ClientMajorVersion(target.ExecutablePath), ct), ct);

    public Task<string> CopyToGameAsync(GameEntry source, GameEntry target, IReadOnlyList<AddonDiff> addons, bool copySettings, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (!File.Exists(target.ExecutablePath))
                throw new AppException($"{target.Name}'s executable wasn't found.");

            var names = addons.Select(a => a.Name).ToList();
            string? skipped = null;
            ChangeGameFiles(target, names, copySettings, null, () =>
            {
                AddonFiles.ApplyAddons(AddonFiles.GameAddOnsDir(GameDir(source)), AddonFiles.GameAddOnsDir(GameDir(target)), addons, CancellationToken.None);
                if (copySettings)
                    skipped = CopyAddonSettings(source, target, names);
            }, ct);

            var message = $"Copied {addons.Count.Plural("addon")} to {target.Name}.";
            return skipped == null ? message : $"{message} Settings weren't copied: {skipped}.";
        }, ct);

    /// <summary>Replaces the installed addons in each update with the downloaded ones.</summary>
    public Task<string> InstallUpdatesAsync(GameEntry game, IReadOnlyList<AddonUpdate> updates, CancellationToken ct) =>
        Task.Run(() =>
        {
            var addOns = AddonFiles.GameAddOnsDir(GameDir(game));
            ChangeGameFiles(game, updates.SelectMany(u => u.Changed).Select(a => a.Name), includeWtf: false, null, () =>
            {
                foreach (var update in updates)
                    AddonFiles.ApplyAddons(update.Dir!, addOns, update.Changed, CancellationToken.None);
            }, ct);
            return $"Updated {string.Join(", ", updates.Select(u => u.Repo))}.";
        }, ct);

    /// <summary>The game's backups, newest first.</summary>
    public List<BackupInfo> ListBackups(GameEntry game)
    {
        var dir = BackupDir(game);
        if (!Directory.Exists(dir))
            return [];
        return Directory.GetFiles(dir, "*.zip")
            .Select(path => (path, contents: AddonFiles.BackupContents(path)))
            .Select(b => new BackupInfo(b.path, File.GetCreationTime(b.path), b.contents.Addons, b.contents.HasSettings))
            .OrderByDescending(b => b.Created)
            .ToList();
    }

    /// <summary>Restores a backup, after backing up what it's about to replace so the restore itself can be undone.</summary>
    public Task<string> RestoreBackupAsync(GameEntry game, BackupInfo backup, CancellationToken ct) =>
        Task.Run(() =>
        {
            ChangeGameFiles(game, backup.Addons, backup.HasSettings, null,
                () => AddonFiles.RestoreBackup(backup.Path, GameDir(game), CancellationToken.None), ct);
            return $"Restored the backup from {backup.Created:g}. What it replaced was backed up first.";
        }, ct);

    /// <summary>Deletes the game's local copy of its repo and its backups, once it's been removed from the library.</summary>
    public void DeleteLocalData(GameEntry game)
    {
        foreach (var dir in new[] { _git.GetLocalCachePath(game), BackupDir(game) })
            if (Directory.Exists(dir))
                AddonFiles.ForceDeleteDirectory(dir);
    }

    public List<CharacterFolder> ListCharacters(GameEntry game) =>
        AddonFiles.Characters(GameDir(game)).Select(c => new CharacterFolder(game, c.Dir, c.Label)).ToList();

    public Task<string> CopyCharacterSetupAsync(CharacterFolder source, IReadOnlyList<CharacterFolder> targets, bool enabledAddons, bool addonData, CancellationToken ct) =>
        Task.Run(() =>
        {
            var data = addonData ? AddonFiles.CharacterAddonData(source.Dir) : [];
            foreach (var game in targets.Select(t => t.Game).Distinct())
            {
                ChangeGameFiles(game, [], includeWtf: true, null, () =>
                {
                    foreach (var target in targets.Where(t => t.Game == game))
                        AddonFiles.CopyCharacterSetup(source.Dir, target.Dir, enabledAddons, data);
                }, ct);
            }
            return $"Gave {targets.Count.Plural("character")} {source.Label}'s addon setup.";
        }, ct);

    /// <summary>Commits whatever changed in the cache and pushes it. Returns the new head (unchanged if there was nothing to commit).</summary>
    private async Task<string?> CommitAndPushAsync(SyncSession s, string message, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Committing...");
        var add = await _git.AddAllAsync(s.CachePath, ct);
        if (!add.Success)
            throw new AppException($"Git add failed: {add.Output.Trim()}");
        if (await _git.IsStatusCleanAsync(s.CachePath, ct))
            return s.HeadCommit;

        var commit = await _git.CommitAsync(s.CachePath, message, ct);
        if (!commit.Success)
            throw new AppException($"Git commit failed: {commit.Output.Trim()}");

        progress?.Report("Pushing...");
        var push = await _git.PushAsync(s.CachePath, SyncBranch, ct);
        if (!push.Success)
            throw new AppException("Push failed. Check that you have write access to the repo, or try again if someone " +
                $"else pushed in the meantime. {push.Output.Trim()}");

        return await _git.GetHeadAsync(s.CachePath, ct);
    }

    /// <summary>Learns the repo's newest commit without touching the local copy, so the game's card can show changes waiting.</summary>
    public async Task RefreshRemoteHeadAsync(GameEntry game, CancellationToken ct)
    {
        if (await _git.IsGitInstalledAsync(ct))
            game.RemoteHead = await _git.GetRemoteHeadAsync(game.SyncRepoUrl!.Trim(), SyncBranch, ct);
    }

    private async Task MarkSyncedAsync(GameEntry game, string? head)
    {
        game.LastSynced = DateTime.UtcNow;
        game.LastSyncedCommit = head;
        game.RemoteHead = head;
        await _gameRepository.SaveAsync();
    }

    internal static string GameDir(GameEntry game) => Path.GetDirectoryName(game.ExecutablePath) ?? "";

    /// <summary>
    /// The one way a game's addons and settings get changed: check nothing they touch is open elsewhere, back them up,
    /// then <paramref name="apply"/>. Once files start changing, finishing beats stopping halfway, so that part
    /// ignores Cancel; if it fails anyway, the message says how to get back what was there.
    /// </summary>
    private static void ChangeGameFiles(GameEntry game, IEnumerable<string> addonNames, bool includeWtf, IProgress<string>? progress,
        Action apply, CancellationToken ct)
    {
        var names = addonNames.ToList();
        var gameDir = GameDir(game);
        progress?.Report("Backing up...");
        AddonFiles.ThrowIfInUse(gameDir, names, includeWtf);
        AddonFiles.Backup(BackupDir(game), gameDir, names, includeWtf, ct);

        progress?.Report("Applying...");
        try
        {
            apply();
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw new AppException($"Stopped partway through changing {game.Name}'s files: {ex.Message} " +
                "Restore a Backup on the Sync tab puts back what was there before.", ex);
        }
        AddonFiles.PruneBackups(BackupDir(game));
    }

    private static string BackupDir(GameEntry game) => Path.Combine(BackupRoot, game.Id.ToString());

    /// <summary>Copies account-wide SavedVariables for the named addons. Returns why it couldn't, or null on success.</summary>
    private static string? CopyAddonSettings(GameEntry source, GameEntry target, IEnumerable<string> addons)
    {
        var from = AddonFiles.FindAccountDir(GameDir(source), source.AccountName.NullIfBlank());
        if (from == null)
            return $"couldn't tell which account folder in {source.Name} to copy from (set its account name)";

        // A client that has never been logged into has no account folder yet; WoW names it after the account in upper case
        var targetAccount = target.AccountName.NullIfBlank();
        var to = AddonFiles.FindAccountDir(GameDir(target), targetAccount)
            ?? (targetAccount == null ? null : Path.Combine(AddonFiles.WtfAccountDir(GameDir(target)), targetAccount.ToUpperInvariant()));
        if (to == null)
            return $"couldn't tell which account folder in {target.Name} to copy into (set its account name)";

        AddonFiles.CopySavedVariables(from, to, addons);
        return null;
    }

    private static string Describe(IReadOnlyList<AddonDiff> addons, SyncOptions options)
    {
        int Count(AddonChange change) => addons.Count(a => a.Change == change);
        var text = addons.Count == 0
            ? "No addon changes"
            : $"Addons: {Count(AddonChange.New)} added, {Count(AddonChange.Changed)} updated, {Count(AddonChange.Removed)} removed";

        var settings = new[] { options.AddonSettings ? "addon" : null, options.CharacterSettings ? "character" : null }
            .OfType<string>().ToList();
        return settings.Count == 0 ? $"{text}." : $"{text}; {string.Join(" and ", settings)} settings synced.";
    }
}
