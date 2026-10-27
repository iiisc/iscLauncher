using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

/// <summary>Points a game's realmlist.wtf and Config.wtf at its server and account.</summary>
public class RealmlistService
{
    /// <summary>Writes the game's realmlist and account settings. Failures throw <see cref="AppException"/>.</summary>
    public async Task ApplyAsync(GameEntry game)
    {
        try
        {
            await UpdateRealmlistAsync(game.GameDir, game.RealmlistAddress);
            await UpdateConfigAsync(game.GameDir, game.AccountName, game.RealmName, game.RealmlistAddress);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AppException($"Couldn't update the game's settings: {ex.Message} Try running as administrator.");
        }
        catch (Exception ex)
        {
            throw new AppException($"Couldn't update the game's realmlist and settings: {ex.Message}");
        }
    }

    /// <summary>Updates every locale's realmlist.wtf under Data, or creates Data\enUS\realmlist.wtf if there's none.</summary>
    private static async Task UpdateRealmlistAsync(string gameDir, string? realmlistAddress)
    {
        if (string.IsNullOrWhiteSpace(realmlistAddress))
            return;

        var dataDir = Path.Combine(gameDir, "Data");
        var content = $"set realmlist {realmlistAddress}";
        var filesUpdated = 0;

        if (Directory.Exists(dataDir))
        {
            foreach (var localeDir in Directory.GetDirectories(dataDir))
            {
                var realmlistPath = Path.Combine(localeDir, "realmlist.wtf");
                if (File.Exists(realmlistPath))
                {
                    await File.WriteAllTextAsync(realmlistPath, content);
                    filesUpdated++;
                }
            }
        }

        if (filesUpdated == 0)
        {
            var fallbackDir = Path.Combine(dataDir, "enUS");
            Directory.CreateDirectory(fallbackDir);
            await File.WriteAllTextAsync(Path.Combine(fallbackDir, "realmlist.wtf"), content);
        }
    }

    /// <summary>
    /// Sets the account name, realm name and realmlist in WTF\Config.wtf, else WTF\config.txt, creating Config.wtf if
    /// neither exists.
    /// </summary>
    private static async Task UpdateConfigAsync(string gameDir, string? accountName, string? realmName, string? realmlistAddress)
    {
        if (string.IsNullOrWhiteSpace(accountName) && string.IsNullOrWhiteSpace(realmName) && string.IsNullOrWhiteSpace(realmlistAddress))
            return;

        var wtfDir = Path.Combine(gameDir, "WTF");
        var configPath = Path.Combine(wtfDir, "Config.wtf");
        var legacyConfigPath = Path.Combine(wtfDir, "config.txt");
        if (!File.Exists(configPath) && File.Exists(legacyConfigPath))
            configPath = legacyConfigPath;

        // A fresh install has no config until the client's first run; start one so that run already logs in correctly
        Directory.CreateDirectory(wtfDir);
        var content = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : "";
        content = SetConfigValue(content, "accountName", accountName);
        content = SetConfigValue(content, "realmName", realmName);
        content = SetConfigValue(content, "realmList", realmlistAddress);
        await File.WriteAllTextAsync(configPath, content);
    }

    /// <summary>Replaces the first <c>SET key "..."</c> line, or appends one. Blank values leave the content untouched.</summary>
    private static string SetConfigValue(string content, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return content;

        var pattern = new Regex($@"^SET {key} ""[^""]*""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var line = $@"SET {key} ""{value}""";
        if (pattern.IsMatch(content))
            // An evaluator inserts the line literally; a replacement string would expand $ sequences in the value
            return pattern.Replace(content, _ => line, 1);
        return content.Length == 0 ? line : content + Environment.NewLine + line;
    }
}
