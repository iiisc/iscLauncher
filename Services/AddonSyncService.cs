using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public record SyncResult(bool Success, string Message);

public class AddonSyncService
{
    private readonly GitService _git;
    private readonly GameRepository _gameRepository = new();

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".old", ".bak"
    };

    private static readonly string BackupRoot = Path.Combine(GameRepository.AppDataFolder, "Backups");

    private const string AddonListHeader = "Addons:";

    public AddonSyncService(GitService git)
    {
        _git = git;
    }

    public async Task<SyncResult> SyncAsync(GameEntry game, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var (error, gameDir, cachePath) = await PrepareAsync(game, progress, ct);
        if (error != null)
            return error;

        ct.ThrowIfCancellationRequested();

        var (addOnsCopied, charactersSynced) = await ApplyCacheToGameAsync(game, gameDir, cachePath, progress, ct);
        await MarkSyncedAsync(game);

        var message = charactersSynced > 0
            ? $"Synced {addOnsCopied} addon(s), {charactersSynced} character(s) restored."
            : $"Synced {addOnsCopied} addon(s). No WTF snapshot found in repo.";
        return new SyncResult(true, message);
    }

    public async Task<SyncResult> UploadAsync(GameEntry game, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var (error, gameDir, cachePath) = await PrepareAsync(game, progress, ct);
        if (error != null)
            return error;

        ct.ThrowIfCancellationRequested();

        progress?.Report("Exporting addons...");
        var addonNames = new List<string>();
        var cacheAddOns = Path.Combine(cachePath, "AddOns");
        var localAddOns = LocalAddOnsDir(gameDir);

        if (Directory.Exists(cacheAddOns))
            await Task.Run(() => GitService.ForceDeleteDirectory(cacheAddOns), ct);
        Directory.CreateDirectory(cacheAddOns);

        if (Directory.Exists(localAddOns))
        {
            await Task.Run(() =>
            {
                foreach (var addonDir in Directory.GetDirectories(localAddOns))
                {
                    ct.ThrowIfCancellationRequested();
                    var addonName = Path.GetFileName(addonDir);
                    if (addonName.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase))
                        continue;
                    CopyDirectoryRecursive(addonDir, Path.Combine(cacheAddOns, addonName), excludeBackups: false, ct);
                    addonNames.Add(addonName);
                }
            }, ct);
        }
        var addOnsCopied = addonNames.Count;

        progress?.Report("Snapshotting WTF settings...");
        var cacheWtf = Path.Combine(cachePath, "WTF");
        var localWtfAccount = WtfAccountDir(gameDir);

        if (Directory.Exists(cacheWtf))
            await Task.Run(() => GitService.ForceDeleteDirectory(cacheWtf), ct);

        var charactersSynced = 0;
        if (Directory.Exists(localWtfAccount))
        {
            await Task.Run(() => CopyDirectoryRecursive(localWtfAccount, WtfAccountDir(cachePath), excludeBackups: true, ct), ct);
            charactersSynced = await Task.Run(() => EnumerateCharacterFolders(localWtfAccount).Count(), ct);
        }

        var gitignorePath = Path.Combine(cachePath, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            await File.WriteAllTextAsync(gitignorePath,
                "*.old\n*.bak\n*.lua.bak\nThumbs.db\n.DS_Store\n", ct);
        }

        progress?.Report("Committing changes...");
        var addResult = await _git.AddAllAsync(cachePath, ct);
        if (!addResult.Success)
            return new SyncResult(false, $"Git add failed: {addResult.Output}");

        if (await _git.IsStatusCleanAsync(cachePath, ct))
            return new SyncResult(true, "Already up to date — nothing to push.");

        var pcName = await _gameRepository.GetComputerNameAsync();
        var subject = $"sync: {game.Name} — {addOnsCopied} addon(s), {charactersSynced} character(s) [{pcName}]";
        var addonLines = addonNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => $"  - {n}");
        var commitMessage = string.Join("\n", new[] { subject, "", AddonListHeader }.Concat(addonLines));

        var commitResult = await _git.CommitAsync(cachePath, commitMessage, ct);
        if (!commitResult.Success)
            return new SyncResult(false, $"Git commit failed: {commitResult.Output}");

        progress?.Report("Pushing to remote...");
        var pushResult = await _git.PushAsync(cachePath, ct);
        if (!pushResult.Success)
            return new SyncResult(false, $"Push failed — check that you have write access to the repo. {pushResult.Output}");

        await MarkSyncedAsync(game);
        return new SyncResult(true, $"Pushed {addOnsCopied} addon(s) and {charactersSynced} character(s).");
    }

    public async Task<SyncResult> RollbackAsync(GameEntry game, string commitHash, string commitMessage, string commitBody = "", IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var (error, gameDir, cachePath) = await PrepareAsync(game, progress, ct);
        if (error != null)
            return error;

        progress?.Report($"Restoring to {commitHash}...");
        var checkoutResult = await _git.CheckoutCommitFilesAsync(cachePath, commitHash, ct);
        if (!checkoutResult.Success)
            return new SyncResult(false, $"Rollback failed: {checkoutResult.Output}");

        progress?.Report("Committing rollback...");
        var addResult = await _git.AddAllAsync(cachePath, ct);
        if (!addResult.Success)
            return new SyncResult(false, $"Git add failed: {addResult.Output}");

        if (!await _git.IsStatusCleanAsync(cachePath, ct))
        {
            var pcName = await _gameRepository.GetComputerNameAsync();
            var subject = $"rollback: {game.Name} — restore to {commitHash[..Math.Min(7, commitHash.Length)]}: {commitMessage} [{pcName}]";
            var rollbackMessage = string.IsNullOrWhiteSpace(commitBody) ? subject : $"{subject}\n\n{commitBody}";
            var commitResult = await _git.CommitAsync(cachePath, rollbackMessage, ct);
            if (!commitResult.Success)
                return new SyncResult(false, $"Git commit failed: {commitResult.Output}");

            progress?.Report("Pushing rollback...");
            var pushResult = await _git.PushAsync(cachePath, ct);
            if (!pushResult.Success)
                return new SyncResult(false, $"Push failed: {pushResult.Output}");
        }

        var (addOnsCopied, charactersSynced) = await ApplyCacheToGameAsync(game, gameDir, cachePath, progress, ct);
        await MarkSyncedAsync(game);

        var message = charactersSynced > 0
            ? $"Rolled back and applied: {commitMessage}. {addOnsCopied} addon(s), {charactersSynced} character(s) restored."
            : $"Rolled back and applied: {commitMessage}. {addOnsCopied} addon(s) restored.";
        return new SyncResult(true, message);
    }

    public async Task<List<GitCommitEntry>> GetCommitLogAsync(GameEntry game, CancellationToken ct = default)
    {
        var cachePath = _git.GetLocalCachePath(game);
        if (!Directory.Exists(Path.Combine(cachePath, ".git")))
            return [];

        await _git.PullAsync(cachePath, ct);
        return await _git.LogAsync(cachePath, 20, ct);
    }

    public int GetRepoCharacterCount(GameEntry game) =>
        EnumerateCharacterFolders(WtfAccountDir(_git.GetLocalCachePath(game))).Count();

    public async Task<List<string>> GetRepoAddonListAsync(GameEntry game, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            return [];

        var cachePath = _git.GetLocalCachePath(game);
        var gitResult = await EnsureCacheRepoAsync(game, cachePath, ct, progress);
        if (!gitResult.Success)
            return [];

        return SubdirectoryNames(Path.Combine(cachePath, "AddOns"));
    }

    public List<string> GetLocalAddonList(GameEntry game)
    {
        var gameDir = Path.GetDirectoryName(game.ExecutablePath);
        return string.IsNullOrEmpty(gameDir) ? [] : SubdirectoryNames(LocalAddOnsDir(gameDir));
    }

    /// <summary>Reads back the addon list that <see cref="UploadAsync"/> writes into the commit body.</summary>
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

    private async Task<(SyncResult? Error, string GameDir, string CachePath)> PrepareAsync(GameEntry game, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Checking prerequisites...");
        if (!await _git.IsGitInstalledAsync(ct))
            return (new SyncResult(false, "Git is required for addon sync. Install from git-scm.com."), "", "");

        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            return (new SyncResult(false, "No sync repo URL configured."), "", "");

        var gameDir = Path.GetDirectoryName(game.ExecutablePath);
        if (string.IsNullOrEmpty(gameDir) || !File.Exists(game.ExecutablePath))
            return (new SyncResult(false, "Game executable not found."), "", "");

        var cachePath = _git.GetLocalCachePath(game);
        var gitResult = await EnsureCacheRepoAsync(game, cachePath, ct, progress);
        if (!gitResult.Success)
            return (new SyncResult(false, $"Git sync failed: {gitResult.Output}"), "", "");

        return (null, gameDir, cachePath);
    }

    private async Task<(int AddOnsCopied, int CharactersSynced)> ApplyCacheToGameAsync(
        GameEntry game, string gameDir, string cachePath, IProgress<string>? progress, CancellationToken ct)
    {
        var wtfDir = Path.Combine(gameDir, "WTF");
        if (Directory.Exists(wtfDir))
        {
            progress?.Report("Backing up WTF settings...");
            await Task.Run(() => BackupWtfFolder(wtfDir, game), ct);
        }

        progress?.Report("Syncing addons...");
        var addOnsCopied = 0;
        var cacheAddOns = Path.Combine(cachePath, "AddOns");
        var localAddOns = LocalAddOnsDir(gameDir);
        if (Directory.Exists(cacheAddOns))
        {
            Directory.CreateDirectory(localAddOns);
            await Task.Run(() =>
            {
                foreach (var addonDir in Directory.GetDirectories(cacheAddOns))
                {
                    ct.ThrowIfCancellationRequested();
                    CopyDirectoryRecursive(addonDir, Path.Combine(localAddOns, Path.GetFileName(addonDir)), excludeBackups: false, ct);
                    addOnsCopied++;
                }
            }, ct);
        }

        progress?.Report("Restoring WTF settings...");
        var charactersSynced = 0;
        var cacheWtfAccount = WtfAccountDir(cachePath);
        if (Directory.Exists(cacheWtfAccount))
        {
            await Task.Run(() => CopyDirectoryRecursive(cacheWtfAccount, WtfAccountDir(gameDir), excludeBackups: true, ct), ct);
            charactersSynced = await Task.Run(() => EnumerateCharacterFolders(cacheWtfAccount).Count(), ct);
        }

        return (addOnsCopied, charactersSynced);
    }

    private async Task MarkSyncedAsync(GameEntry game)
    {
        game.LastSynced = DateTime.UtcNow;
        await _gameRepository.UpdateGameAsync(game);
    }

    private async Task<GitResult> EnsureCacheRepoAsync(GameEntry game, string cachePath, CancellationToken ct, IProgress<string>? progress)
    {
        var branch = string.IsNullOrWhiteSpace(game.SyncBranch) ? "main" : game.SyncBranch;

        if (Directory.Exists(Path.Combine(cachePath, ".git")))
        {
            var currentRemote = await _git.GetRemoteUrlAsync(cachePath, ct);
            if (currentRemote != null && !string.Equals(currentRemote.TrimEnd('/'), game.SyncRepoUrl!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Repo URL changed — re-cloning...");
                await Task.Run(() => GitService.ForceDeleteDirectory(cachePath), ct);
                return await _git.CloneAsync(game.SyncRepoUrl!, branch, cachePath, ct);
            }

            progress?.Report("Pulling latest changes...");
            var pullResult = await _git.PullAsync(cachePath, ct);
            // Pull can fail on empty repos (no commits yet) — that's fine for upload flow
            return pullResult.Success ? pullResult : new GitResult(true, "Empty repo — will create first commit.");
        }

        progress?.Report("Cloning repository...");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        return await _git.CloneAsync(game.SyncRepoUrl!, branch, cachePath, ct, progress);
    }

    private static string LocalAddOnsDir(string gameDir) => Path.Combine(gameDir, "Interface", "AddOns");

    private static string WtfAccountDir(string root) => Path.Combine(root, "WTF", "Account");

    private static List<string> SubdirectoryNames(string dir)
    {
        if (!Directory.Exists(dir))
            return [];

        return Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, bool excludeBackups, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            if (excludeBackups && ExcludedExtensions.Contains(Path.GetExtension(file))) continue;
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(destFile))
                File.SetAttributes(destFile, FileAttributes.Normal);
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, Path.GetFileName(dir)), excludeBackups, ct);
        }
    }

    private static IEnumerable<string> EnumerateCharacterFolders(string wtfAccountDir)
    {
        if (!Directory.Exists(wtfAccountDir))
            yield break;

        foreach (var accountDir in Directory.GetDirectories(wtfAccountDir))
        {
            foreach (var serverDir in Directory.GetDirectories(accountDir))
            {
                // SavedVariables sits beside the realm folders but holds no characters
                if (Path.GetFileName(serverDir).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var charDir in Directory.GetDirectories(serverDir))
                    yield return charDir;
            }
        }
    }

    private static void BackupWtfFolder(string wtfDir, GameEntry game)
    {
        var accountDir = Path.Combine(wtfDir, "Account");
        if (!Directory.Exists(accountDir))
            return;

        var backupDir = Path.Combine(BackupRoot, game.Id.ToString());
        Directory.CreateDirectory(backupDir);

        // Keep only last 2 backups
        var existing = Directory.GetFiles(backupDir, "*.zip")
            .OrderByDescending(f => f)
            .Skip(1)
            .ToList();
        foreach (var old in existing)
        {
            try { File.Delete(old); } catch { }
        }

        var zipPath = Path.Combine(backupDir, $"wtf-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        try
        {
            ZipFile.CreateFromDirectory(accountDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
        }
        catch
        {
            // Non-fatal — don't block sync if backup fails
        }
    }
}
