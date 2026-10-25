using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace iscLauncher.Services;

/// <summary>
/// Service to manage realmlist.wtf and config.txt files for WoW-style games.
/// Updates configuration files to point to the correct server and account before launching.
/// </summary>
public class RealmlistService
{
    /// <summary>
    /// Updates realmlist.wtf files for the given game executable.
    /// Scans all locale subdirectories under {executableDirectory}\Data\ for existing files.
    /// Falls back to creating Data\enUS\realmlist.wtf if none are found.
    /// </summary>
    public async Task<RealmlistResult> UpdateRealmlistAsync(string executablePath, string? realmlistAddress)
    {
        if (string.IsNullOrWhiteSpace(realmlistAddress))
        {
            return new RealmlistResult(true, "No realmlist configured, skipping.");
        }

        try
        {
            var executableDirectory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(executableDirectory))
            {
                return new RealmlistResult(false, "Could not determine executable directory.");
            }

            var dataDir = Path.Combine(executableDirectory, "Data");
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
                var fallbackDir = Path.Combine(executableDirectory, "Data", "enUS");
                Directory.CreateDirectory(fallbackDir);
                await File.WriteAllTextAsync(Path.Combine(fallbackDir, "realmlist.wtf"), content);
            }

            return new RealmlistResult(true, $"Realmlist updated to {realmlistAddress}");
        }
        catch (UnauthorizedAccessException)
        {
            return new RealmlistResult(false, "Access denied when updating realmlist.wtf. Try running as administrator.");
        }
        catch (Exception ex)
        {
            return new RealmlistResult(false, $"Failed to update realmlist: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the config file with the account name, realm name, and optionally the realmlist.
    /// Looks for {executableDirectory}\WTF\Config.wtf, then WTF\config.txt.
    /// </summary>
    public async Task<RealmlistResult> UpdateConfigAsync(string executablePath, string? accountName, string? realmName, string? realmlistAddress)
    {
        if (string.IsNullOrWhiteSpace(accountName) && string.IsNullOrWhiteSpace(realmName) && string.IsNullOrWhiteSpace(realmlistAddress))
        {
            return new RealmlistResult(true, "No config updates needed, skipping.");
        }

        try
        {
            var executableDirectory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(executableDirectory))
            {
                return new RealmlistResult(false, "Could not determine executable directory.");
            }

            var configPath = Path.Combine(executableDirectory, "WTF", "Config.wtf");
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(executableDirectory, "WTF", "config.txt");
            }

            if (!File.Exists(configPath))
            {
                return new RealmlistResult(false, $"Config file not found in WTF folder. Looked for Config.wtf and config.txt");
            }

            var content = await File.ReadAllTextAsync(configPath);
            content = SetConfigValue(content, "accountName", accountName);
            content = SetConfigValue(content, "realmName", realmName);
            content = SetConfigValue(content, "realmList", realmlistAddress);
            await File.WriteAllTextAsync(configPath, content);

            return new RealmlistResult(true, $"Config updated successfully.");
        }
        catch (UnauthorizedAccessException)
        {
            return new RealmlistResult(false, "Access denied when updating config.txt. Try running as administrator.");
        }
        catch (Exception ex)
        {
            return new RealmlistResult(false, $"Failed to update config: {ex.Message}");
        }
    }

    /// <summary>Replaces the first <c>SET key "..."</c> line, or appends one. Blank values leave the content untouched.</summary>
    private static string SetConfigValue(string content, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return content;

        var pattern = new Regex($@"^SET {key} ""[^""]*""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var line = $@"SET {key} ""{value}""";
        var replaced = pattern.Replace(content, line, 1);
        return replaced != content ? replaced : content + Environment.NewLine + line;
    }
}

public record RealmlistResult(bool Success, string Message);
