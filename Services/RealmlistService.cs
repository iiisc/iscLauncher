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
    /// Updates the realmlist.wtf file for the given game executable.
    /// The file is expected at: {executableDirectory}\Data\enUS\realmlist.wtf
    /// </summary>
    /// <param name="executablePath">Full path to the game executable</param>
    /// <param name="realmlistAddress">The server address (e.g., "logon.warmane.com")</param>
    /// <returns>Result indicating success or failure</returns>
    public async Task<RealmlistResult> UpdateRealmlistAsync(string executablePath, string realmlistAddress)
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

            var realmlistPath = Path.Combine(executableDirectory, "Data", "enUS", "realmlist.wtf");

            // Create directory if it doesn't exist
            var realmlistDirectory = Path.GetDirectoryName(realmlistPath);
            if (!string.IsNullOrEmpty(realmlistDirectory) && !Directory.Exists(realmlistDirectory))
            {
                Directory.CreateDirectory(realmlistDirectory);
            }

            // Write the realmlist content
            var content = $"set realmlist {realmlistAddress}";
            await File.WriteAllTextAsync(realmlistPath, content);

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
    /// Updates the config.txt file with the account name, realm name, and optionally the realmlist.
    /// The file is expected at: {executableDirectory}\WTF\config.txt
    /// </summary>
    /// <param name="executablePath">Full path to the game executable</param>
    /// <param name="accountName">The account name to set</param>
    /// <param name="realmName">The realm/server name to set</param>
    /// <param name="realmlistAddress">Optional realmlist address to also update in config.txt</param>
    /// <returns>Result indicating success or failure</returns>
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

            // Try alternative filename if Config.wtf doesn't exist
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(executableDirectory, "WTF", "config.txt");
            }

            // Check if config file exists
            if (!File.Exists(configPath))
            {
                return new RealmlistResult(false, $"Config file not found in WTF folder. Looked for Config.wtf and config.txt");
            }

            var content = await File.ReadAllTextAsync(configPath);
            var updated = false;

            // Update accountName if provided
            if (!string.IsNullOrWhiteSpace(accountName))
            {
                var accountPattern = new Regex(@"^SET accountName ""[^""]*""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (accountPattern.IsMatch(content))
                {
                    content = accountPattern.Replace(content, $@"SET accountName ""{accountName}""");
                    updated = true;
                }
                else
                {
                    // Add the setting if it doesn't exist
                    content += $@"{Environment.NewLine}SET accountName ""{accountName}""";
                    updated = true;
                }
            }

            // Update realmName in config.txt if provided
            if (!string.IsNullOrWhiteSpace(realmName))
            {
                var realmNamePattern = new Regex(@"^SET realmName ""[^""]*""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (realmNamePattern.IsMatch(content))
                {
                    content = realmNamePattern.Replace(content, $@"SET realmName ""{realmName}""");
                    updated = true;
                }
                else
                {
                    // Add the setting if it doesn't exist
                    content += $@"{Environment.NewLine}SET realmName ""{realmName}""";
                    updated = true;
                }
            }

            // Update realmList in config.txt if provided
            if (!string.IsNullOrWhiteSpace(realmlistAddress))
            {
                var realmPattern = new Regex(@"^SET realmList ""[^""]*""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (realmPattern.IsMatch(content))
                {
                    content = realmPattern.Replace(content, $@"SET realmList ""{realmlistAddress}""");
                    updated = true;
                }
            }

            if (updated)
            {
                await File.WriteAllTextAsync(configPath, content);
            }

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
}

public record RealmlistResult(bool Success, string Message);
