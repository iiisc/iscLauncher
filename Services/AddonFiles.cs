using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using iscLauncher.Models;

namespace iscLauncher.Services;

public enum AddonChange { New, Changed, Removed }

/// <summary>An addon that differs between a source and a target AddOns folder. Versions come from the addons' .toc files.</summary>
public record AddonDiff(string Name, AddonChange Change, string? SourceVersion, string? TargetVersion, string? Warning)
{
    /// <summary>"old → new", when both versions are known and differ.</summary>
    public string? VersionChange => SourceVersion != null && TargetVersion != null && SourceVersion != TargetVersion
        ? $"{TargetVersion} → {SourceVersion}"
        : null;
}

/// <param name="Characters">Characters in the source's WTF folder, for the character-settings option.</param>
public record AddonPreview(List<AddonDiff> Diffs, int Unchanged, int Characters = 0);

public record SyncOptions(bool AddonSettings, bool CharacterSettings);

/// <summary>Path-level addon and WTF operations shared by repo sync and game-to-game copy.</summary>
public static partial class AddonFiles
{
    private const int CompareBufferSize = 64 * 1024;
    // Older pushes let git convert line endings, so a repo's text files can differ from the game's only in CRLF vs LF.
    // WoW reads both the same, so for these that doesn't count as a change; other files must match byte for byte.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".lua", ".toc", ".xml", ".txt", ".md" };

    private static readonly HashSet<string> BackupExtensions = new(StringComparer.OrdinalIgnoreCase) { ".old", ".bak" };
    private static readonly Regex TocField = new(@"^##\s*(Interface|Version)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    // WoW treats any field starting with "Dep" as a required dependency, as well as RequiredDeps
    private static readonly Regex DependencyField = new(@"^##\s*(Dep\w*|RequiredDeps)\s*:\s*(.*)$", RegexOptions.IgnoreCase);

    public static string GameAddOnsDir(string gameDir) => Path.Combine(gameDir, "Interface", "AddOns");

    public static string GameAddOnsDir(GameEntry game) => GameAddOnsDir(AddonSyncService.GameDir(game));

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

    /// <summary>Major client version from the game exe (1 = Vanilla, 3 = Wrath, ...), or 0 if it has none.</summary>
    public static int ClientMajorVersion(string exePath)
    {
        try
        {
            return VersionParts(exePath) is [var major, ..] ? int.Parse(major) : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The client's version, e.g. "3.3.5", or null if the exe has none.</summary>
    public static string? ClientVersion(string exePath)
    {
        try
        {
            var parts = VersionParts(exePath);
            return parts.Count >= 3 ? string.Join('.', parts.Take(3)) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    // Blizzard's official exes store 1.60.1 as file-version major 160, so the version text is what's reliable.
    // Private-server clients write it as "3, 3, 5, 12340".
    private static List<string> VersionParts(string exePath) =>
        Regex.Matches(FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "", @"\d+").Select(m => m.Value).ToList();

    internal static void ForceDeleteDirectory(string path)
    {
        // Directory.Delete refuses read-only files, which git creates for its objects, and read-only folders,
        // which some addon downloads come with
        var root = new DirectoryInfo(path);
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(root))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        Directory.Delete(path, true);
    }

    /// <summary>The version an addon's .toc gives, and the GitHub repo it names as its home, if any.</summary>
    public static (string? Version, string? Repo) AddonInfo(string addonDir)
    {
        var toc = ReadToc(addonDir);
        return (toc.Version, toc.Repo);
    }

    /// <summary>
    /// Groups addons that ship as several folders (AtlasLoot, AtlasLoot_Crafting, ...), so they're shown and updated
    /// together. Each group leads with its shortest name.
    /// </summary>
    public static IEnumerable<List<T>> Families<T>(IEnumerable<T> addons, Func<T, string> name) =>
        addons.GroupBy(a => name(a).Split('_')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => name(a).Length).ThenBy(name, StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>The addons in <paramref name="addOnsDir"/> that <paramref name="addons"/> need to load, directly or through each other.</summary>
    public static HashSet<string> Dependencies(string addOnsDir, IEnumerable<string> addons)
    {
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(addons);
        while (queue.TryDequeue(out var name))
        {
            var dir = Path.Combine(addOnsDir, name);
            if (!Directory.Exists(dir))
                continue;
            foreach (var dependency in ReadToc(dir).Dependencies)
            {
                if (needed.Add(dependency))
                    queue.Enqueue(dependency);
            }
        }
        return needed;
    }

    private record TocInfo(string? Version, List<int> Interfaces, string? Repo, List<string> Dependencies);

    private static TocInfo ReadToc(string addonDir)
    {
        string? version = null;
        string? repo = null;
        var interfaces = new List<int>();
        var dependencies = new List<string>();
        var name = Path.GetFileName(addonDir);

        // Addons can ship one .toc per client flavor (Name_Vanilla.toc, Name-Wrath.toc), so collect interfaces from all of them
        foreach (var toc in Directory.GetFiles(addonDir, "*.toc"))
        {
            var isPrimary = Path.GetFileNameWithoutExtension(toc).Equals(name, StringComparison.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(toc).Take(50))
            {
                // Only the header names a home; lines below it list the addon's files
                if (line.StartsWith("##"))
                {
                    repo ??= GitHubRepo(line);
                    if (DependencyField.Match(line) is { Success: true } dependency)
                        dependencies.AddRange(dependency.Groups[2].Value.Split(',').Select(d => d.Trim()).Where(d => d.Length > 0));
                }
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
        return new TocInfo(version, interfaces, repo, dependencies);
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
            if (!filesB.TryGetValue(relative, out var fileB) || (fileA.Length != fileB.Length && !IsText(relative)))
                return false;
        }

        // Only read contents once names and sizes all match, since that's the slow part
        var bufferA = new byte[CompareBufferSize];
        var bufferB = new byte[CompareBufferSize];
        foreach (var (relative, fileA) in filesA)
        {
            ct.ThrowIfCancellationRequested();
            var fileB = filesB[relative];
            var equal = (fileA.Length == fileB.Length && ContentsEqual(fileA.FullName, fileB.FullName, bufferA, bufferB))
                || (IsText(relative) && TextEqualIgnoringLineEndings(fileA.FullName, fileB.FullName));
            if (!equal)
                return false;
        }
        return true;
    }

    // FileInfo from enumeration already carries the size, so comparing lengths costs no extra file system calls
    private static Dictionary<string, FileInfo> RelativeFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f.FullName), StringComparer.OrdinalIgnoreCase);

    private static bool IsText(string path) => TextExtensions.Contains(Path.GetExtension(path));

    private static bool TextEqualIgnoringLineEndings(string pathA, string pathB)
    {
        var a = File.ReadAllBytes(pathA);
        var b = File.ReadAllBytes(pathB);
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && a[i] == '\r') i++;
            while (j < b.Length && b[j] == '\r') j++;
            if (i == a.Length || j == b.Length)
                return i == a.Length && j == b.Length;
            if (a[i++] != b[j++])
                return false;
        }
    }

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
}
