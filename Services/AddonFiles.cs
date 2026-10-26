using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace iscLauncher.Services;

public enum AddonChange { New, Changed, Removed }

/// <summary>An addon that differs between a source and a target AddOns folder. Versions come from the addons' .toc files.</summary>
public record AddonDiff(string Name, AddonChange Change, string? SourceVersion, string? TargetVersion, string? Warning);

/// <param name="Characters">Characters in the source's WTF folder, for the character-settings option.</param>
public record AddonPreview(List<AddonDiff> Diffs, int Unchanged, int Characters = 0);

public record SyncOptions(bool AddonSettings, bool CharacterSettings);

/// <summary>Thrown for expected sync failures; the message is shown to the user as-is.</summary>
public class SyncException(string message) : Exception(message);

/// <summary>Path-level addon and WTF operations shared by repo sync and game-to-game copy.</summary>
public static class AddonFiles
{
    private const string SavedVariables = "SavedVariables";
    private const int BackupsToKeep = 3;
    private const int CompareBufferSize = 64 * 1024;

    private static readonly HashSet<string> BackupExtensions = new(StringComparer.OrdinalIgnoreCase) { ".old", ".bak" };
    private static readonly Regex TocField = new(@"^##\s*(Interface|Version)\s*:\s*(.*)$", RegexOptions.IgnoreCase);

    public static string GameAddOnsDir(string gameDir) => Path.Combine(gameDir, "Interface", "AddOns");

    public static string WtfAccountDir(string root) => Path.Combine(root, "WTF", "Account");

    /// <summary>Installed addon folder names. Blizzard_ folders ship with the client, so they're never compared or moved.</summary>
    public static List<string> AddonNames(string addOnsDir)
    {
        if (!Directory.Exists(addOnsDir))
            return [];

        return NonBlizzard(Directory.GetDirectories(addOnsDir).Select(d => Path.GetFileName(d)));
    }

    private static List<string> NonBlizzard(IEnumerable<string> names) =>
        names.Where(n => !n.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What would change if the addons in <paramref name="sourceDir"/> were applied to <paramref name="targetDir"/>.
    /// Removed means only the target has it. Pass a <paramref name="clientMajor"/> of 0 when the target isn't a game client.
    /// </summary>
    public static AddonPreview Compare(string sourceDir, string targetDir, bool includeRemoved, int clientMajor, CancellationToken ct)
    {
        var sourceNames = AddonNames(sourceDir);
        var targetNames = new HashSet<string>(AddonNames(targetDir), StringComparer.OrdinalIgnoreCase);
        var diffs = new List<AddonDiff>();
        var unchanged = 0;

        foreach (var name in sourceNames)
        {
            ct.ThrowIfCancellationRequested();
            var source = Path.Combine(sourceDir, name);
            var target = Path.Combine(targetDir, name);
            var exists = targetNames.Contains(name);
            if (exists && DirectoriesEqual(source, target, ct))
            {
                unchanged++;
                continue;
            }

            var toc = ReadToc(source);
            diffs.Add(new AddonDiff(name, exists ? AddonChange.Changed : AddonChange.New, toc.Version,
                exists ? ReadToc(target).Version : null, CompatibilityWarning(toc.Interfaces, clientMajor)));
        }

        if (includeRemoved)
        {
            foreach (var name in targetNames.Except(sourceNames, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                diffs.Add(new AddonDiff(name, AddonChange.Removed, null, ReadToc(Path.Combine(targetDir, name)).Version, null));
        }

        return new AddonPreview(diffs, unchanged);
    }

    /// <summary>Makes each selected addon in <paramref name="targetDir"/> match <paramref name="sourceDir"/>; Removed ones are deleted.</summary>
    public static void ApplyAddons(string sourceDir, string targetDir, IEnumerable<AddonDiff> selected, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var addon in selected)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(targetDir, addon.Name);
            // Replace the whole folder so files a newer version dropped don't linger
            if (Directory.Exists(target))
                ForceDeleteDirectory(target);
            if (addon.Change != AddonChange.Removed)
                CopyDirectory(Path.Combine(sourceDir, addon.Name), target, skipBackups: false, ct);
        }
    }

    /// <summary>
    /// Copies the chosen settings categories between two WTF\Account folders. With <paramref name="mirror"/>, target
    /// content in those categories that the source doesn't have is deleted, so the repo matches what was pushed.
    /// </summary>
    public static void CopyWtf(string sourceAccountRoot, string targetAccountRoot, SyncOptions options, bool mirror, CancellationToken ct)
    {
        if (mirror)
        {
            foreach (var entry in SettingsEntries(targetAccountRoot, options).ToList())
            {
                if (Directory.Exists(entry))
                    ForceDeleteDirectory(entry);
                else
                    DeleteFile(entry);
            }
            foreach (var account in AccountDirs(targetAccountRoot).Where(a => Directory.GetFileSystemEntries(a).Length == 0))
                Directory.Delete(account);
        }

        foreach (var entry in SettingsEntries(sourceAccountRoot, options))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(targetAccountRoot, Path.GetRelativePath(sourceAccountRoot, entry));
            if (Directory.Exists(entry))
                CopyDirectory(entry, target, skipBackups: true, ct);
            else
                CopyFile(entry, target, skipBackups: true);
        }
    }

    /// <summary>Copies the SavedVariables files of the named addons between two account folders or two character folders.</summary>
    public static void CopySavedVariables(string sourceDir, string targetDir, IEnumerable<string> addons)
    {
        foreach (var name in addons)
        {
            var file = Path.Combine(sourceDir, SavedVariables, name + ".lua");
            if (File.Exists(file))
                CopyFile(file, Path.Combine(targetDir, SavedVariables, name + ".lua"));
        }
    }

    /// <summary>The WTF\Account folder matching <paramref name="accountName"/>, else the only one there is.</summary>
    public static string? FindAccountDir(string gameDir, string? accountName)
    {
        var accounts = AccountDirs(WtfAccountDir(gameDir));
        return accounts.FirstOrDefault(a => Path.GetFileName(a).Equals(accountName, StringComparison.OrdinalIgnoreCase))
            ?? (accounts.Count == 1 ? accounts[0] : null);
    }

    /// <summary>
    /// Character folders under <paramref name="root"/>\WTF\Account, with a display label. Older clients nest them as
    /// Account\Realm\Name; newer ones as Account\&lt;number&gt;\Name-Suffix, where the folder name alone identifies the character.
    /// </summary>
    public static List<(string Dir, string Label)> Characters(string root)
    {
        var characters = new List<(string Dir, string Label)>();
        foreach (var account in AccountDirs(WtfAccountDir(root)))
        {
            foreach (var group in Directory.GetDirectories(account).Where(d => !IsSavedVariables(d)))
            {
                var groupName = Path.GetFileName(group);
                foreach (var character in Directory.GetDirectories(group))
                {
                    var name = Path.GetFileName(character);
                    characters.Add((character, groupName.All(char.IsDigit) ? name : $"{name} ({groupName})"));
                }
            }
        }
        return characters;
    }

    public static int CharacterCount(string root) => Characters(root).Count;

    /// <summary>Addons with per-character data in a character folder. Blizzard's own UI data stays with the character.</summary>
    public static List<string> CharacterAddonData(string characterDir)
    {
        var dir = Path.Combine(characterDir, SavedVariables);
        if (!Directory.Exists(dir))
            return [];

        return NonBlizzard(Directory.GetFiles(dir, "*.lua").Select(f => Path.GetFileNameWithoutExtension(f)));
    }

    /// <summary>Gives a character another's enabled-addon list (AddOns.txt) and/or its per-character data for the named addons.</summary>
    public static void CopyCharacterSetup(string sourceCharacterDir, string targetCharacterDir, bool enabledAddons, IEnumerable<string> addonData)
    {
        var addOnsTxt = Path.Combine(sourceCharacterDir, "AddOns.txt");
        if (enabledAddons && File.Exists(addOnsTxt))
            CopyFile(addOnsTxt, Path.Combine(targetCharacterDir, "AddOns.txt"));
        CopySavedVariables(sourceCharacterDir, targetCharacterDir, addonData);
    }

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
                AddToZip(zip, wtf, "WTF/Account", ct);
            foreach (var name in addons)
                AddToZip(zip, Path.Combine(addOnsDir, name), $"AddOns/{name}", ct);
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

    /// <summary>Major client version from the game exe (1 = Vanilla, 3 = Wrath, ...), or 0 if it has none.</summary>
    public static int ClientMajorVersion(string exePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exePath).FileMajorPart;
        }
        catch
        {
            return 0;
        }
    }

    internal static void ForceDeleteDirectory(string path)
    {
        // Directory.Delete refuses read-only files, which git creates for its objects
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                file.Attributes = FileAttributes.Normal;
        }
        Directory.Delete(path, true);
    }

    /// <summary>
    /// The WTF\Account entries in the chosen categories. Account-wide SavedVariables are addon settings; everything else
    /// in an account (realm/character folders, macros, bindings) is character settings.
    /// </summary>
    private static IEnumerable<string> SettingsEntries(string accountRoot, SyncOptions options)
    {
        // Newer clients also keep a SavedVariables folder directly under Account, beside the account folders
        var shared = Path.Combine(accountRoot, SavedVariables);
        if (options.AddonSettings && Directory.Exists(shared))
            yield return shared;

        foreach (var account in AccountDirs(accountRoot))
        {
            foreach (var entry in Directory.GetFileSystemEntries(account))
            {
                if (IsSavedVariables(entry) ? options.AddonSettings : options.CharacterSettings)
                    yield return entry;
            }
        }
    }

    private static List<string> AccountDirs(string accountRoot) =>
        Directory.Exists(accountRoot) ? Directory.GetDirectories(accountRoot).Where(d => !IsSavedVariables(d)).ToList() : [];

    private static bool IsSavedVariables(string path) =>
        Path.GetFileName(path).Equals(SavedVariables, StringComparison.OrdinalIgnoreCase);

    private record TocInfo(string? Version, List<int> Interfaces);

    private static TocInfo ReadToc(string addonDir)
    {
        string? version = null;
        var interfaces = new List<int>();
        var name = Path.GetFileName(addonDir);

        // Addons can ship one .toc per client flavor (Name_Vanilla.toc, Name-Wrath.toc), so collect interfaces from all of them
        foreach (var toc in Directory.GetFiles(addonDir, "*.toc"))
        {
            var isPrimary = Path.GetFileNameWithoutExtension(toc).Equals(name, StringComparison.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(toc).Take(50))
            {
                var match = TocField.Match(line);
                if (!match.Success)
                    continue;

                var value = match.Groups[2].Value.Trim();
                if (match.Groups[1].Value.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                    interfaces.AddRange(value.Split(',').Select(v => int.TryParse(v.Trim(), out var i) ? i : 0).Where(i => i > 0));
                else if (isPrimary || version == null)
                    version = value;
            }
        }
        return new TocInfo(version, interfaces);
    }

    private static string? CompatibilityWarning(List<int> interfaces, int clientMajor)
    {
        // Interface numbers encode the client version: 11200 = 1.12, 30300 = 3.3, 110002 = 11.0.2
        var majors = interfaces.Select(i => i / 10000).Distinct().Order().ToList();
        if (clientMajor == 0 || majors.Count == 0 || majors.Contains(clientMajor))
            return null;
        return $"made for client {string.Join("/", majors.Select(m => $"{m}.x"))}, this game is {clientMajor}.x";
    }

    private static bool DirectoriesEqual(string a, string b, CancellationToken ct)
    {
        var filesA = RelativeFiles(a);
        var filesB = RelativeFiles(b);
        if (filesA.Count != filesB.Count)
            return false;

        foreach (var (relative, fileA) in filesA)
        {
            if (!filesB.TryGetValue(relative, out var fileB) || fileA.Length != fileB.Length)
                return false;
        }

        // Only read contents once names and sizes all match, since that's the slow part
        var bufferA = new byte[CompareBufferSize];
        var bufferB = new byte[CompareBufferSize];
        foreach (var (relative, fileA) in filesA)
        {
            ct.ThrowIfCancellationRequested();
            if (!ContentsEqual(fileA.FullName, filesB[relative].FullName, bufferA, bufferB))
                return false;
        }
        return true;
    }

    // FileInfo from enumeration already carries the size, so comparing lengths costs no extra file system calls
    private static Dictionary<string, FileInfo> RelativeFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f.FullName), StringComparer.OrdinalIgnoreCase);

    private static bool ContentsEqual(string pathA, string pathB, byte[] bufferA, byte[] bufferB)
    {
        using var streamA = File.OpenRead(pathA);
        using var streamB = File.OpenRead(pathB);
        while (true)
        {
            var readA = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            var readB = streamB.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                return false;
            if (readA == 0)
                return true;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir, bool skipBackups, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            CopyFile(file, Path.Combine(targetDir, Path.GetFileName(file)), skipBackups);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)), skipBackups, ct);
    }

    private static void CopyFile(string source, string target, bool skipBackups = false)
    {
        if (skipBackups && BackupExtensions.Contains(Path.GetExtension(source)))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
            File.SetAttributes(target, FileAttributes.Normal);
        File.Copy(source, target, overwrite: true);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void AddToZip(ZipArchive zip, string dir, string prefix, CancellationToken ct)
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(file, $"{prefix}/{Path.GetRelativePath(dir, file).Replace('\\', '/')}", CompressionLevel.Fastest);
        }
    }
}
