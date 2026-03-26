using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public record SyncResult(bool Success, string Message, int AddOnsCopied, int CharactersSynced);

public class AddonSyncService
{
    private readonly GitService _git;
    private readonly GameRepository _gameRepository = new();

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".old", ".bak"
    };

    public AddonSyncService(GitService git)
    {
        _git = git;
    }

    public async Task<SyncResult> SyncAsync(GameEntry game, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // 1. Validate
        progress?.Report("Checking prerequisites...");
        if (!await _git.IsGitInstalledAsync(ct))
            return new SyncResult(false, "Git is required for addon sync. Install from git-scm.com.", 0, 0);

        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            return new SyncResult(false, "No sync repo URL configured.", 0, 0);

        var gameDir = Path.GetDirectoryName(game.ExecutablePath);
        if (string.IsNullOrEmpty(gameDir) || !File.Exists(game.ExecutablePath))
            return new SyncResult(false, "Game executable not found.", 0, 0);

        // 2. Clone or Pull
        var cachePath = _git.GetLocalCachePath(game);
        var gitResult = await EnsureCacheRepoAsync(game, cachePath, ct, progress);
        if (!gitResult.Success)
            return new SyncResult(false, $"Git sync failed: {gitResult.Output}", 0, 0);

        ct.ThrowIfCancellationRequested();

        // 3. Backup WTF before overwriting
        var wtfDir = Path.Combine(gameDir, "WTF");
        if (Directory.Exists(wtfDir))
        {
            progress?.Report("Backing up WTF settings...");
            BackupWtfFolder(wtfDir, game);
        }

        // 4. Sync AddOns
        progress?.Report("Syncing addons...");
        var addOnsCopied = 0;
        var cacheAddOns = Path.Combine(cachePath, "AddOns");
        var localAddOns = Path.Combine(gameDir, "Interface", "AddOns");
        if (Directory.Exists(cacheAddOns))
        {
            Directory.CreateDirectory(localAddOns);
            foreach (var addonDir in Directory.GetDirectories(cacheAddOns))
            {
                ct.ThrowIfCancellationRequested();
                var addonName = Path.GetFileName(addonDir);
                var targetDir = Path.Combine(localAddOns, addonName);
                CopyDirectoryRecursive(addonDir, targetDir, excludeBackups: false, ct);
                addOnsCopied++;
            }
        }

        // 5. Sync Account-Level Files
        progress?.Report("Syncing account settings...");
        var cacheAccountTemplate = Path.Combine(cachePath, "WTF", "AccountTemplate");
        var wtfAccountDir = Path.Combine(gameDir, "WTF", "Account");

        if (Directory.Exists(cacheAccountTemplate) && Directory.Exists(wtfAccountDir))
        {
            foreach (var accountDir in Directory.GetDirectories(wtfAccountDir))
            {
                ct.ThrowIfCancellationRequested();
                CopyDirectoryRecursive(cacheAccountTemplate, accountDir, excludeBackups: true, ct);
            }
        }

        // 6. Fan-Out Character Settings
        progress?.Report("Syncing character settings...");
        var charactersSynced = 0;
        var cacheCharTemplate = Path.Combine(cachePath, "WTF", "CharacterTemplate");

        if (Directory.Exists(cacheCharTemplate) && Directory.Exists(wtfAccountDir))
        {
            foreach (var charPath in EnumerateCharacterFolders(wtfAccountDir))
            {
                ct.ThrowIfCancellationRequested();
                CopyDirectoryRecursive(cacheCharTemplate, charPath, excludeBackups: true, ct);
                charactersSynced++;
            }
        }

        // 7. Update timestamp
        game.LastSynced = DateTime.UtcNow;
        await _gameRepository.UpdateGameAsync(game);

        var message = $"Synced {addOnsCopied} addon(s), settings applied to {charactersSynced} character(s).";
        if (!Directory.Exists(wtfAccountDir))
            message += " No WTF/Account folders found — addons synced only.";

        return new SyncResult(true, message, addOnsCopied, charactersSynced);
    }

    public async Task<SyncResult> UploadAsync(GameEntry game, string sourceCharacterPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // 1. Validate
        progress?.Report("Checking prerequisites...");
        if (!await _git.IsGitInstalledAsync(ct))
            return new SyncResult(false, "Git is required for addon sync. Install from git-scm.com.", 0, 0);

        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            return new SyncResult(false, "No sync repo URL configured.", 0, 0);

        var gameDir = Path.GetDirectoryName(game.ExecutablePath);
        if (string.IsNullOrEmpty(gameDir) || !File.Exists(game.ExecutablePath))
            return new SyncResult(false, "Game executable not found.", 0, 0);

        if (!Directory.Exists(sourceCharacterPath))
            return new SyncResult(false, $"Character folder not found: {sourceCharacterPath}", 0, 0);

        // 2. Clone or Pull
        var cachePath = _git.GetLocalCachePath(game);
        var gitResult = await EnsureCacheRepoAsync(game, cachePath, ct, progress);
        if (!gitResult.Success)
            return new SyncResult(false, $"Git sync failed: {gitResult.Output}", 0, 0);

        ct.ThrowIfCancellationRequested();

        // 3. Export AddOns (skip Blizzard_* folders)
        progress?.Report("Exporting addons...");
        var addonNames = new List<string>();
        var cacheAddOns = Path.Combine(cachePath, "AddOns");
        var localAddOns = Path.Combine(gameDir, "Interface", "AddOns");

        if (Directory.Exists(cacheAddOns))
            Directory.Delete(cacheAddOns, true);
        Directory.CreateDirectory(cacheAddOns);

        if (Directory.Exists(localAddOns))
        {
            foreach (var addonDir in Directory.GetDirectories(localAddOns))
            {
                ct.ThrowIfCancellationRequested();
                var addonName = Path.GetFileName(addonDir);
                if (addonName.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase))
                    continue;
                var targetDir = Path.Combine(cacheAddOns, addonName);
                CopyDirectoryRecursive(addonDir, targetDir, excludeBackups: false, ct);
                addonNames.Add(addonName);
            }
        }
        var addOnsCopied = addonNames.Count;

        // 4. Export Account Template
        progress?.Report("Exporting account settings...");
        var cacheAccountTemplate = Path.Combine(cachePath, "WTF", "AccountTemplate");
        var accountDir = FindParentAccountFolder(sourceCharacterPath, gameDir);

        if (Directory.Exists(cacheAccountTemplate))
            Directory.Delete(cacheAccountTemplate, true);
        Directory.CreateDirectory(cacheAccountTemplate);

        if (accountDir != null && Directory.Exists(accountDir))
        {
            CopyAccountLevelFiles(accountDir, cacheAccountTemplate, ct);
        }

        // 5. Export Character Template
        progress?.Report("Exporting character settings...");
        var cacheCharTemplate = Path.Combine(cachePath, "WTF", "CharacterTemplate");

        if (Directory.Exists(cacheCharTemplate))
            Directory.Delete(cacheCharTemplate, true);
        Directory.CreateDirectory(cacheCharTemplate);

        CopyDirectoryRecursive(sourceCharacterPath, cacheCharTemplate, excludeBackups: true, ct);
        var charFileCount = Directory.GetFiles(cacheCharTemplate, "*", SearchOption.AllDirectories).Length;

        // 6. Ensure .gitignore exists
        var gitignorePath = Path.Combine(cachePath, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            await File.WriteAllTextAsync(gitignorePath,
                "*.old\n*.bak\n*.lua.bak\nThumbs.db\n.DS_Store\n", ct);
        }

        // 7. Commit & Push
        progress?.Report("Committing changes...");
        var addResult = await _git.AddAllAsync(cachePath, ct);
        if (!addResult.Success)
            return new SyncResult(false, $"Git add failed: {addResult.Output}", 0, 0);

        if (await _git.IsStatusCleanAsync(cachePath, ct))
            return new SyncResult(true, "Already up to date — nothing to push.", addOnsCopied, 0);

        // Build a descriptive commit message with addon list in body
        var charName = Path.GetFileName(sourceCharacterPath);
        var serverName = Path.GetFileName(Path.GetDirectoryName(sourceCharacterPath) ?? "");
        var pcName = await _gameRepository.GetComputerNameAsync();
        var subject = $"sync: {game.Name} — {addOnsCopied} addon(s), template from {charName}@{serverName} [{pcName}]";

        var bodyLines = new List<string> { "", "Addons:" };
        foreach (var name in addonNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            bodyLines.Add($"  - {name}");

        var commitMessage = subject + "\n" + string.Join("\n", bodyLines);

        var commitResult = await _git.CommitAsync(cachePath, commitMessage, ct);
        if (!commitResult.Success)
            return new SyncResult(false, $"Git commit failed: {commitResult.Output}", 0, 0);

        progress?.Report("Pushing to remote...");
        var pushResult = await _git.PushAsync(cachePath, ct);
        if (!pushResult.Success)
            return new SyncResult(false, $"Push failed — check that you have write access to the repo. {pushResult.Output}", 0, 0);

        // 8. Update timestamp
        game.LastSynced = DateTime.UtcNow;
        await _gameRepository.UpdateGameAsync(game);

        return new SyncResult(true, $"Pushed {addOnsCopied} addon(s) and character template ({charFileCount} files).", addOnsCopied, charFileCount);
    }

    private async Task<GitResult> EnsureCacheRepoAsync(GameEntry game, string cachePath, CancellationToken ct, IProgress<string>? progress)
    {
        var branch = string.IsNullOrWhiteSpace(game.SyncBranch) ? "main" : game.SyncBranch;

        if (Directory.Exists(Path.Combine(cachePath, ".git")))
        {
            // Check for remote URL mismatch
            var currentRemote = await _git.GetRemoteUrlAsync(cachePath, ct);
            if (currentRemote != null && !string.Equals(currentRemote.TrimEnd('/'), game.SyncRepoUrl!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Repo URL changed — re-cloning...");
                Directory.Delete(cachePath, true);
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

    private static string? FindParentAccountFolder(string characterPath, string gameDir)
    {
        var wtfAccountDir = Path.Combine(gameDir, "WTF", "Account");
        if (!Directory.Exists(wtfAccountDir))
            return null;

        // characterPath is e.g. <gameDir>/WTF/Account/ISC3S/Onyxia/Isc
        // We need the account folder: <gameDir>/WTF/Account/ISC3S
        var relativePath = Path.GetRelativePath(wtfAccountDir, characterPath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length >= 1)
            return Path.Combine(wtfAccountDir, parts[0]);

        return null;
    }

    private static void CopyAccountLevelFiles(string accountDir, string targetDir, CancellationToken ct)
    {
        // Copy files (excluding .old and .bak, and excluding server subdirectories' contents)
        foreach (var file in Directory.GetFiles(accountDir))
        {
            ct.ThrowIfCancellationRequested();
            if (IsExcludedFile(file)) continue;
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        // Copy SavedVariables subfolder only
        var savedVarsDir = Path.Combine(accountDir, "SavedVariables");
        if (Directory.Exists(savedVarsDir))
        {
            var targetSavedVars = Path.Combine(targetDir, "SavedVariables");
            CopyDirectoryRecursive(savedVarsDir, targetSavedVars, excludeBackups: true, ct);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, bool excludeBackups, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            if (excludeBackups && IsExcludedFile(file)) continue;
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, dirName), excludeBackups, ct);
        }
    }

    private static bool IsExcludedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ExcludedExtensions.Contains(ext))
            return true;

        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".lua.bak", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> EnumerateCharacterFolders(string wtfAccountDir)
    {
        if (!Directory.Exists(wtfAccountDir))
            yield break;

        foreach (var accountDir in Directory.GetDirectories(wtfAccountDir))
        {
            foreach (var serverDir in Directory.GetDirectories(accountDir))
            {
                var serverName = Path.GetFileName(serverDir);
                // Skip well-known account-level subdirectories
                if (serverName.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var charDir in Directory.GetDirectories(serverDir))
                {
                    yield return charDir;
                }
            }
        }
    }

    public async Task<List<GitCommitEntry>> GetCommitLogAsync(GameEntry game, CancellationToken ct = default)
    {
        var cachePath = _git.GetLocalCachePath(game);
        if (!Directory.Exists(Path.Combine(cachePath, ".git")))
            return [];

        await _git.PullAsync(cachePath, ct);
        return await _git.LogAsync(cachePath, 20, ct);
    }

    public async Task<List<string>> GetRepoAddonListAsync(GameEntry game, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            return [];

        var cachePath = _git.GetLocalCachePath(game);
        var gitResult = await EnsureCacheRepoAsync(game, cachePath, ct, progress);
        if (!gitResult.Success)
            return [];

        var cacheAddOns = Path.Combine(cachePath, "AddOns");
        if (!Directory.Exists(cacheAddOns))
            return [];

        return Directory.GetDirectories(cacheAddOns)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public async Task<SyncResult> RollbackAsync(GameEntry game, string commitHash, string commitMessage, string commitBody = "", IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Checking prerequisites...");
        if (!await _git.IsGitInstalledAsync(ct))
            return new SyncResult(false, "Git is required for addon sync. Install from git-scm.com.", 0, 0);

        if (string.IsNullOrWhiteSpace(game.SyncRepoUrl))
            return new SyncResult(false, "No sync repo URL configured.", 0, 0);

        var cachePath = _git.GetLocalCachePath(game);
        var gitResult = await EnsureCacheRepoAsync(game, cachePath, ct, progress);
        if (!gitResult.Success)
            return new SyncResult(false, $"Git sync failed: {gitResult.Output}", 0, 0);

        progress?.Report($"Restoring to {commitHash}...");
        var checkoutResult = await _git.CheckoutCommitFilesAsync(cachePath, commitHash, ct);
        if (!checkoutResult.Success)
            return new SyncResult(false, $"Rollback failed: {checkoutResult.Output}", 0, 0);

        progress?.Report("Committing rollback...");
        var addResult = await _git.AddAllAsync(cachePath, ct);
        if (!addResult.Success)
            return new SyncResult(false, $"Git add failed: {addResult.Output}", 0, 0);

        if (await _git.IsStatusCleanAsync(cachePath, ct))
            return new SyncResult(true, "Already at that commit — nothing to rollback.", 0, 0);

        var pcName = await _gameRepository.GetComputerNameAsync();
        var subject = $"rollback: {game.Name} — restore to {commitHash[..Math.Min(7, commitHash.Length)]}: {commitMessage} [{pcName}]";
        var rollbackMessage = string.IsNullOrWhiteSpace(commitBody) ? subject : $"{subject}\n\n{commitBody}";
        var commitResult = await _git.CommitAsync(cachePath, rollbackMessage, ct);
        if (!commitResult.Success)
            return new SyncResult(false, $"Git commit failed: {commitResult.Output}", 0, 0);

        progress?.Report("Pushing rollback...");
        var pushResult = await _git.PushAsync(cachePath, ct);
        if (!pushResult.Success)
            return new SyncResult(false, $"Push failed: {pushResult.Output}", 0, 0);

        game.LastSynced = DateTime.UtcNow;
        await _gameRepository.UpdateGameAsync(game);

        return new SyncResult(true, $"Rolled back to: {commitMessage}", 0, 0);
    }

    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IscLauncher", "Backups");

    private static void BackupWtfFolder(string wtfDir, GameEntry game)
    {
        var accountDir = Path.Combine(wtfDir, "Account");
        if (!Directory.Exists(accountDir))
            return;

        var backupDir = Path.Combine(BackupRoot, game.Id.ToString());
        Directory.CreateDirectory(backupDir);

        // Keep only last 5 backups
        var existing = Directory.GetFiles(backupDir, "*.zip")
            .OrderByDescending(f => f)
            .Skip(4)
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
