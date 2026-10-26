using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace iscLauncher.Services;

public static partial class AddonFiles
{
    private const int BackupsToKeep = 3;

    /// <summary>
    /// Zips what an operation is about to overwrite in a game: its WTF\Account folder and the named addon folders.
    /// Keeps the newest few in <paramref name="backupDir"/>. Throws on failure, since the caller is about to delete these files.
    /// </summary>
    public static void Backup(string backupDir, string gameDir, IEnumerable<string> addonNames, bool includeWtf, CancellationToken ct)
    {
        var addOnsDir = GameAddOnsDir(gameDir);
        var addons = addonNames.Where(n => Directory.Exists(Path.Combine(addOnsDir, n))).ToList();
        var wtf = WtfAccountDir(gameDir);
        includeWtf &= Directory.Exists(wtf);
        if (addons.Count == 0 && !includeWtf)
            return;

        Directory.CreateDirectory(backupDir);
        var zipPath = Path.Combine(backupDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            if (includeWtf)
                AddToZip(zip, wtf, ZipSettings, ct);
            foreach (var name in addons)
                AddToZip(zip, Path.Combine(addOnsDir, name), $"{ZipAddOns}/{name}", ct);
        }
        catch
        {
            DeleteFile(zipPath);
            throw;
        }

        // Pruning is housekeeping; a locked old backup shouldn't fail the operation
        foreach (var old in Directory.GetFiles(backupDir, "*.zip").OrderByDescending(File.GetCreationTimeUtc).Skip(BackupsToKeep))
            try { DeleteFile(old); } catch { }
    }

    // Where a backup zip keeps each part. Settings sat under Account/ at the root in older versions.
    private const string ZipSettings = "WTF/Account";
    private const string ZipAddOns = "AddOns";

    /// <summary>The addons a backup holds, and whether it holds the WTF\Account settings.</summary>
    public static (List<string> Addons, bool HasSettings) BackupContents(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return BackupContents(zip);
    }

    private static (List<string> Addons, bool HasSettings) BackupContents(ZipArchive zip)
    {
        var addons = zip.Entries.Select(e => e.FullName.Split('/')).Where(p => p.Length > 2 && p[0] == ZipAddOns).Select(p => p[1])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return (addons, zip.Entries.Any(e => BackupSettingsPath(e.FullName) != null));
    }

    /// <summary>
    /// Puts back what a backup holds: its WTF\Account replaces the game's, and each addon in it replaces the game's copy.
    /// Addons installed after the backup was made are left alone, since the backup has nothing to say about them.
    /// </summary>
    public static void RestoreBackup(string zipPath, string gameDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var (addons, hasSettings) = BackupContents(zip);
        // Cleared first, so files added since the backup don't linger among the restored ones
        if (hasSettings && Directory.Exists(WtfAccountDir(gameDir)))
            ForceDeleteDirectory(WtfAccountDir(gameDir));
        foreach (var addon in addons.Select(a => Path.Combine(GameAddOnsDir(gameDir), a)).Where(Directory.Exists))
            ForceDeleteDirectory(addon);

        var root = Path.GetFullPath(gameDir) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            var relative = BackupSettingsPath(entry.FullName)
                ?? (entry.FullName.StartsWith(ZipAddOns + "/") ? Path.Combine("Interface", entry.FullName) : null);
            if (relative == null)
                continue;
            var target = Path.GetFullPath(Path.Combine(gameDir, relative));
            // Never write outside the game folder, whatever the zip says
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string? BackupSettingsPath(string entry) =>
        entry.StartsWith(ZipSettings + "/") ? entry
        : entry.StartsWith("Account/") ? Path.Combine("WTF", entry)
        : null;

    private static void AddToZip(ZipArchive zip, string dir, string prefix, CancellationToken ct)
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(file, $"{prefix}/{Path.GetRelativePath(dir, file).Replace('\\', '/')}", CompressionLevel.Fastest);
        }
    }
}
