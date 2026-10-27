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
    /// Throws on failure, since the caller is about to delete these files.
    /// </summary>
    public static void Backup(string backupDir, string gameDir, IEnumerable<string> addonNames, bool includeWtf, CancellationToken ct)
    {
        var parts = PartsToChange(gameDir, addonNames, includeWtf);
        if (parts.Count == 0)
            return;

        Directory.CreateDirectory(backupDir);
        var zipPath = Path.Combine(backupDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (dir, zipPrefix) in parts)
                AddToZip(zip, dir, zipPrefix, ct);
        }
        catch
        {
            DeleteFile(zipPath);
            throw;
        }
    }

    /// <summary>
    /// Keeps only the newest few backups. Called once an operation has succeeded: until then, the backup from before a
    /// failed attempt may be the only one holding what the game had.
    /// </summary>
    public static void PruneBackups(string backupDir)
    {
        if (!Directory.Exists(backupDir))
            return;
        // Pruning is housekeeping; a locked old backup shouldn't fail the operation
        foreach (var old in Directory.GetFiles(backupDir, "*.zip").OrderByDescending(File.GetCreationTimeUtc).Skip(BackupsToKeep))
            try { DeleteFile(old); } catch { }
    }

    /// <summary>
    /// Throws if another program has open any file that an operation on these addons and settings would change, so it
    /// can stop before changing anything instead of failing halfway through.
    /// </summary>
    public static void ThrowIfInUse(string gameDir, IEnumerable<string> addonNames, bool includeWtf)
    {
        foreach (var (dir, _) in PartsToChange(gameDir, addonNames, includeWtf))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                // 32 and 33 are Windows' sharing and lock violations: some other program has the file open
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
                {
                    throw new AppException($"{Path.GetRelativePath(gameDir, file)} is open in another program, so nothing was changed. " +
                        "Close whatever is using it (the game, an editor, a virus scan) and try again.");
                }
            }
        }
    }

    // The folders an operation on these addons and settings changes, each with where a backup zip keeps it
    private static List<(string Dir, string ZipPrefix)> PartsToChange(string gameDir, IEnumerable<string> addonNames, bool includeWtf)
    {
        var parts = new List<(string, string)>();
        if (includeWtf && Directory.Exists(WtfAccountDir(gameDir)))
            parts.Add((WtfAccountDir(gameDir), ZipSettings));
        foreach (var name in addonNames)
        {
            var dir = Path.Combine(GameAddOnsDir(gameDir), name);
            if (Directory.Exists(dir))
                parts.Add((dir, $"{ZipAddOns}/{name}"));
        }
        return parts;
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
        return (addons, zip.Entries.Any(e => IsSettingsEntry(e.FullName)));
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

        foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            var relative = IsSettingsEntry(entry.FullName) ? entry.FullName
                : entry.FullName.StartsWith(ZipAddOns + "/") ? Path.Combine("Interface", entry.FullName)
                : null;
            if (relative != null)
                ExtractInside(entry, gameDir, relative);
        }
    }

    // A zip can name paths that climb out of the folder it's unpacked into; those entries are skipped
    private static void ExtractInside(ZipArchiveEntry entry, string dir, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(dir, relative));
        if (!target.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
    }

    private static bool IsSettingsEntry(string entry) => entry.StartsWith(ZipSettings + "/");

    private static void AddToZip(ZipArchive zip, string dir, string prefix, CancellationToken ct)
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(file, $"{prefix}/{Path.GetRelativePath(dir, file).Replace('\\', '/')}", CompressionLevel.Fastest);
        }
    }
}
