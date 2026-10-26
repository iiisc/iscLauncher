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

    /// <summary>The GitHub repos the installed addons' .toc files name as their home.</summary>
    public static List<string> GitHubRepos(string addOnsDir) =>
        AddonNames(addOnsDir)
            .SelectMany(name => Directory.GetFiles(Path.Combine(addOnsDir, name), "*.toc"))
            .SelectMany(toc => File.ReadLines(toc).Take(50).Where(line => line.StartsWith("##")))
            .Select(GitHubRepo)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Unpacks a downloaded zip into <paramref name="addOnsDir"/> as addon folders, wherever they are in it: release zips
    /// have them at the top, GitHub's code zips inside a folder named after the commit, and a repo can be one addon itself.
    /// Returns their names.
    /// </summary>
    public static List<string> ExtractAddons(string zipPath, string addOnsDir)
    {
        var unpacked = addOnsDir + "-unpacked";
        ZipFile.ExtractToDirectory(zipPath, unpacked);
        Directory.CreateDirectory(addOnsDir);
        var names = new List<string>();
        foreach (var dir in AddonDirs(unpacked).ToList())
        {
            var tocs = Directory.GetFiles(dir, "*.toc").Select(t => Path.GetFileNameWithoutExtension(t)).ToList();
            // An addon's folder is named after its .toc (Name.toc, or flavors like Name_Wrath.toc); a repo's top folder isn't
            var name = tocs.Any(t => t.StartsWith(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))
                ? Path.GetFileName(dir)
                : tocs.MinBy(t => t.Length)!;
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            Directory.Move(dir, Path.Combine(addOnsDir, name));
            names.Add(name);
        }
        if (Directory.Exists(unpacked))
            ForceDeleteDirectory(unpacked);
        return names;
    }

    // An addon's own libraries have .toc files too, so the search stops at the first folder with one
    private static IEnumerable<string> AddonDirs(string dir) =>
        Directory.EnumerateFiles(dir, "*.toc").Any() ? [dir] : Directory.GetDirectories(dir).SelectMany(AddonDirs);
}
