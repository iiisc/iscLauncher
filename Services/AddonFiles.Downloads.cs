using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace iscLauncher.Services;

// Addons downloaded from GitHub
public static partial class AddonFiles
{
    private static readonly Regex GitHubRepoPattern = new(@"(?:github\.com/|^)([\w.-]+)/([\w.-]+?)(?:\.git)?(?:[/#?\s]|$)", RegexOptions.IgnoreCase);

    /// <summary>"owner/name" from a GitHub link or from "owner/name" itself, or null if it names no repo.</summary>
    public static string? GitHubRepo(string text) =>
        GitHubRepoPattern.Match(text.Trim()) is { Success: true } match ? $"{match.Groups[1].Value}/{match.Groups[2].Value}" : null;

    /// <summary>
    /// Unpacks a downloaded zip into <paramref name="addOnsDir"/> as addon folders, wherever they are in it: release zips
    /// have them at the top, GitHub's code zips inside a folder named after the commit, and a repo can be one addon itself.
    /// Returns their names.
    /// </summary>
    public static List<string> ExtractAddons(string zipPath, string addOnsDir)
    {
        // Each file is unpacked straight to where it belongs: moving a folder that was just unpacked fails while a virus
        // scanner still has its files open
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => e.Name.Length > 0).Select(e => (Entry: e, Path: e.FullName.Replace('\\', '/'))).ToList();
        var tocsByDir = files.Where(f => f.Path.EndsWith(".toc", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => Parent(f.Path))
            .ToDictionary(g => g.Key, g => g.Select(f => Path.GetFileNameWithoutExtension(f.Entry.Name)).ToList());
        // An addon's own libraries have .toc files too, so only the outermost folders with one are addons
        var addonDirs = tocsByDir.Keys.Where(dir => !tocsByDir.Keys.Any(outer => outer != dir && IsUnder(dir, outer)));

        var names = new List<string>();
        foreach (var dir in addonDirs)
        {
            var tocs = tocsByDir[dir];
            // An addon's folder is named after its .toc (Name.toc, or flavors like Name_Wrath.toc); a repo's top folder isn't
            var folder = dir[(dir.LastIndexOf('/') + 1)..];
            var name = folder.Length > 0 && tocs.Any(t => t.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) ? folder : tocs.MinBy(t => t.Length)!;
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            names.Add(name);

            foreach (var (entry, path) in files.Where(f => IsUnder(f.Path, dir)))
                ExtractInside(entry, Path.Combine(addOnsDir, name), dir.Length == 0 ? path : path[(dir.Length + 1)..]);
        }
        return names;
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static bool IsUnder(string path, string dir) => dir.Length == 0 || path.StartsWith(dir + "/", StringComparison.Ordinal);
}
