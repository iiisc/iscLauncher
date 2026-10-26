using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace iscLauncher.Services;

/// <summary>Points a game's realmlist.wtf and Config.wtf at its server and account. Failures throw <see cref="AppException"/>.</summary>
public class RealmlistService
{
    /// <summary>Updates every locale's realmlist.wtf under Data, or creates Data\enUS\realmlist.wtf if there's none.</summary>
    public async Task UpdateRealmlistAsync(string executablePath, string? realmlistAddress)
    {
        if (string.IsNullOrWhiteSpace(realmlistAddress))
            return;

        try
        {
            var dataDir = Path.Combine(ExecutableDirectory(executablePath), "Data");
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
        catch (UnauthorizedAccessException)
        {
            throw new AppException("Access denied when updating realmlist.wtf. Try running as administrator.");
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw new AppException($"Failed to update realmlist: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the config file with the account name, realm name, and optionally the realmlist.
    /// Uses {executableDirectory}\WTF\Config.wtf, else WTF\config.txt, and creates Config.wtf if neither exists.
    /// </summary>
    public async Task UpdateConfigAsync(string executablePath, string? accountName, string? realmName, string? realmlistAddress)
    {
        if (string.IsNullOrWhiteSpace(accountName) && string.IsNullOrWhiteSpace(realmName) && string.IsNullOrWhiteSpace(realmlistAddress))
            return;

        try
        {
            var wtfDir = Path.Combine(ExecutableDirectory(executablePath), "WTF");
            var configPath = Path.Combine(wtfDir, "Config.wtf");
            var legacyConfigPath = Path.Combine(wtfDir, "config.txt");
            if (!File.Exists(configPath) && File.Exists(legacyConfigPath))
            {
                configPath = legacyConfigPath;
            }

            // A fresh install has no config until the client's first run; start one so that run already logs in correctly
            Directory.CreateDirectory(wtfDir);
            var content = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : "";
            content = SetConfigValue(content, "accountName", accountName);
            content = SetConfigValue(content, "realmName", realmName);
            content = SetConfigValue(content, "realmList", realmlistAddress);
            await File.WriteAllTextAsync(configPath, content);
        }
        catch (UnauthorizedAccessException)
        {
            throw new AppException("Access denied when updating config.txt. Try running as administrator.");
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw new AppException($"Failed to update config: {ex.Message}");
        }
    }

    private static string ExecutableDirectory(string executablePath) =>
        Path.GetDirectoryName(executablePath) is { Length: > 0 } dir
            ? dir
            : throw new AppException("Could not determine executable directory.");

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
