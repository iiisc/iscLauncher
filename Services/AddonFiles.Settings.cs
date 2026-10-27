using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace iscLauncher.Services;

public static partial class AddonFiles
{
    private const string SavedVariables = "SavedVariables";

    public static string WtfAccountDir(string root) => Path.Combine(root, "WTF", "Account");

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
                CopyDirectory(entry, target, ct);
            else
                CopyFile(entry, target);
        }
    }

    /// <summary>Whether two WTF\Account folders hold the same settings in the chosen categories, leaving out the files a sync doesn't copy.</summary>
    public static bool SettingsEqual(string accountRootA, string accountRootB, SyncOptions options, CancellationToken ct) =>
        FilesEqual(SettingsFiles(accountRootA, options), SettingsFiles(accountRootB, options), ct);

    private static Dictionary<string, FileInfo> SettingsFiles(string accountRoot, SyncOptions options) =>
        SettingsEntries(accountRoot, options)
            .SelectMany(entry => Directory.Exists(entry)
                ? new DirectoryInfo(entry).EnumerateFiles("*", SearchOption.AllDirectories)
                : [new FileInfo(entry)])
            .Where(f => !IsUnsynced(f.Name))
            .ToDictionary(f => Path.GetRelativePath(accountRoot, f.FullName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the SavedVariables files of the named addons between two account folders.</summary>
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
    /// How many characters have settings under <paramref name="root"/>\WTF\Account. Older clients nest them as
    /// Account\Realm\Name, newer ones as Account\&lt;number&gt;\Name-Suffix.
    /// </summary>
    public static int CharacterCount(string root) =>
        AccountDirs(WtfAccountDir(root))
            .SelectMany(account => Directory.GetDirectories(account).Where(d => !IsSavedVariables(d)))
            .Sum(group => Directory.GetDirectories(group).Length);

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
}
