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
