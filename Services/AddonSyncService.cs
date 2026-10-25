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
/// Failures throw <see cref="SyncException"/>.
/// </summary>
public class AddonSyncService
{
    private const string AddonListHeader = "Addons:";

    private static readonly string BackupRoot = Path.Combine(GameRepository.AppDataFolder, "Backups");

    private readonly GitService _git;
    private readonly GameRepository _gameRepository = new();

    public AddonSyncService(GitService git)
    {
        _git = git;
    }

    public async Task<SyncSession> OpenAsync(GameEntry game, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Checking prerequisites...");
        if (!await _git.IsGitInstalledAsync(ct))
            throw new SyncException("Git is required for addon sync. Install from git-scm.com.");
        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            throw new SyncException("No sync repo URL configured.");
        if (!File.Exists(game.ExecutablePath))
            throw new SyncException("Game executable not found.");

        progress?.Report("Fetching from repo...");
        var cachePath = _git.GetLocalCachePath(game);
        var mirror = await _git.MirrorAsync(game.SyncRepoUrl.Trim(), Branch(game), cachePath, progress, ct);
        if (!mirror.Success)
            throw new SyncException($"Couldn't fetch from the repo: {mirror.Output.Trim()}");

        return new SyncSession(game, cachePath, await _git.GetHeadAsync(cachePath, ct));
    }

    public Task<AddonPreview> PreviewPullAsync(SyncSession s, CancellationToken ct) =>
        Task.Run(() => AddonFiles.Compare(s.RepoAddOns, s.GameAddOns, includeRemoved: true,
            AddonFiles.ClientMajorVersion(s.Game.ExecutablePath), ct) with { Characters = AddonFiles.CharacterCount(s.CachePath) }, ct);

    public Task<AddonPreview> PreviewPushAsync(SyncSession s, CancellationToken ct) =>
        Task.Run(() => AddonFiles.Compare(s.GameAddOns, s.RepoAddOns, includeRemoved: true, clientMajor: 0, ct)
            with { Characters = AddonFiles.CharacterCount(s.GameDir) }, ct);

    /// <summary>Puts the cache's files back to <paramref name="commit"/> and previews applying them. The next session discards this unless <see cref="RollbackAsync"/> commits it.</summary>
    public async Task<AddonPreview> PreviewRollbackAsync(SyncSession s, string commit, CancellationToken ct)
    {
        var result = await _git.RestoreTreeAsync(s.CachePath, commit, ct);
        if (!result.Success)
            throw new SyncException($"Couldn't load that version: {result.Output.Trim()}");
        return await PreviewPullAsync(s, ct);
    }

    /// <summary>Describes repo changes this PC hasn't pulled yet, or null if it's caught up.</summary>
    public async Task<string?> DescribeUnpulledChangesAsync(SyncSession s, CancellationToken ct)
    {
        if (s.HeadCommit == null || s.HeadCommit == s.Game.LastSyncedCommit)
            return null;

        var latest = (await _git.LogAsync(s.CachePath, 1, ct)).FirstOrDefault();
        return latest == null
            ? null
            : $"⚠ The repo has changes this PC hasn't pulled. Latest: \"{latest.Message}\" ({latest.DisplayDate}). " +
              "Pushing replaces them for everything you select. Pull first to keep them.";
    }

    public async Task<string> PullAsync(SyncSession s, IReadOnlyList<AddonDiff> addons, SyncOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var settings = options.AddonSettings || options.CharacterSettings;

        progress?.Report("Backing up...");
        await Task.Run(() => Backup(s.Game, addons.Select(a => a.Name), includeWtf: settings, ct), ct);

        progress?.Report("Applying...");
        await Task.Run(() =>
        {
            AddonFiles.ApplyAddons(s.RepoAddOns, s.GameAddOns, addons, ct);
            AddonFiles.CopyWtf(s.RepoWtfAccount, s.GameWtfAccount, options, mirror: false, ct);
        }, ct);

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
        var pcName = await _gameRepository.GetComputerNameAsync();
        var subject = $"sync: {s.Game.Name} — {repoAddons.Count} addon(s), {characters} character(s) [{pcName}]";
        var message = string.Join("\n", new[] { subject, "", AddonListHeader }.Concat(repoAddons.Select(n => $"  - {n}")));

        var head = await CommitAndPushAsync(s, message, progress, ct);
        await MarkSyncedAsync(s.Game, head);
        return head == s.HeadCommit ? "Nothing to push — the repo already matches." : $"Pushed. {Describe(addons, options)}";
    }

    public async Task<List<GitCommitEntry>> GetCommitLogAsync(SyncSession s, CancellationToken ct) =>
        await _git.LogAsync(s.CachePath, 20, ct);

    /// <summary>Commits the files restored by <see cref="PreviewRollbackAsync"/> for everyone, then applies the chosen parts to this PC.</summary>
    public async Task<string> RollbackAsync(SyncSession s, GitCommitEntry target, IReadOnlyList<AddonDiff> addons, SyncOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var pcName = await _gameRepository.GetComputerNameAsync();
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
                throw new SyncException($"{target.Name}'s executable wasn't found.");

            var names = addons.Select(a => a.Name).ToList();
            Backup(target, names, includeWtf: copySettings, ct);
            AddonFiles.ApplyAddons(AddonFiles.GameAddOnsDir(GameDir(source)), AddonFiles.GameAddOnsDir(GameDir(target)), addons, ct);

            var message = $"Copied {addons.Count} addon(s) to {target.Name}.";
            if (copySettings && CopyAddonSettings(source, target, names) is { } skipped)
                message += $" Settings weren't copied: {skipped}.";
            return message;
        }, ct);

    public List<CharacterFolder> ListCharacters(GameEntry game) =>
        AddonFiles.Characters(GameDir(game)).Select(c => new CharacterFolder(game, c.Dir, c.Label)).ToList();

    public Task<string> CopyCharacterSetupAsync(CharacterFolder source, IReadOnlyList<CharacterFolder> targets, bool enabledAddons, bool addonData, CancellationToken ct) =>
        Task.Run(() =>
        {
            var data = addonData ? AddonFiles.CharacterAddonData(source.Dir) : [];
            foreach (var game in targets.Select(t => t.Game).Distinct())
                Backup(game, [], includeWtf: true, ct);
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                AddonFiles.CopyCharacterSetup(source.Dir, target.Dir, enabledAddons, data);
            }
            return $"Gave {targets.Count} character(s) {source.Label}'s addon setup.";
        }, ct);

    /// <summary>Reads back the addon list that <see cref="PushAsync"/> writes into the commit body.</summary>
    public static List<string> ParseAddonList(string commitBody)
    {
        var addons = new List<string>();
        if (string.IsNullOrWhiteSpace(commitBody))
            return addons;

        var inAddonSection = false;
        foreach (var rawLine in commitBody.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals(AddonListHeader, StringComparison.OrdinalIgnoreCase))
            {
                inAddonSection = true;
                continue;
            }

            if (inAddonSection)
            {
                if (line.StartsWith("- "))
                    addons.Add(line[2..].Trim());
                else if (line.Length == 0 && addons.Count > 0)
                    break;
            }
        }
        return addons;
    }

    /// <summary>Commits whatever changed in the cache and pushes it. Returns the new head (unchanged if there was nothing to commit).</summary>
    private async Task<string?> CommitAndPushAsync(SyncSession s, string message, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Committing...");
        var add = await _git.AddAllAsync(s.CachePath, ct);
        if (!add.Success)
            throw new SyncException($"Git add failed: {add.Output.Trim()}");
        if (await _git.IsStatusCleanAsync(s.CachePath, ct))
            return s.HeadCommit;

        var commit = await _git.CommitAsync(s.CachePath, message, ct);
        if (!commit.Success)
            throw new SyncException($"Git commit failed: {commit.Output.Trim()}");

        progress?.Report("Pushing...");
        var push = await _git.PushAsync(s.CachePath, Branch(s.Game), ct);
        if (!push.Success)
            throw new SyncException("Push failed. Check that you have write access to the repo, or try again if someone " +
                $"else pushed in the meantime. {push.Output.Trim()}");

        return await _git.GetHeadAsync(s.CachePath, ct);
    }

    private async Task MarkSyncedAsync(GameEntry game, string? head)
    {
        game.LastSynced = DateTime.UtcNow;
        game.LastSyncedCommit = head;
        await _gameRepository.UpdateGameAsync(game);
    }

    internal static string GameDir(GameEntry game) => Path.GetDirectoryName(game.ExecutablePath) ?? "";

    private static string Branch(GameEntry game) => game.SyncBranch.NullIfBlank() ?? "main";

    private static void Backup(GameEntry game, IEnumerable<string> addonNames, bool includeWtf, CancellationToken ct) =>
        AddonFiles.Backup(Path.Combine(BackupRoot, game.Id.ToString()), GameDir(game), addonNames, includeWtf, ct);

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
