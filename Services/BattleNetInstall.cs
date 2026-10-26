using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace iscLauncher.Services;

/// <summary>
/// An official Blizzard install: one folder per game version (_classic_era_, _forever_, ...) under a root holding
/// Blizzard's launcher. These log in through Battle.net, so they're started through it instead of with a password.
/// </summary>
public record BattleNetInstall(string LauncherPath, string? ProductCode)
{
    private static readonly Regex ProductOrFolder = new(@"wow[a-z_]*|_[a-z_]+_");

    public static bool IsOfficial(string exePath) => LauncherFor(exePath) != null;

    /// <summary>
    /// The game exe of every official version on this PC. Battle.net registers each install for uninstalling, which is
    /// more reliable than Blizzard's own registry key: private-server clients overwrite that one with their own folder.
    /// </summary>
    public static List<string> FindInstalledGames()
    {
        using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall == null)
            return [];

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is string displayName && displayName.StartsWith("World of Warcraft", StringComparison.Ordinal)
                && key.GetValue("InstallLocation") is string root && Directory.Exists(root))
                roots.Add(root.TrimEnd('\\'));
        }

        // Wow*.exe skips the crash reporter that sits beside each version's game exe
        return roots
            .SelectMany(root => Directory.GetDirectories(root, "_*_"))
            .SelectMany(dir => Directory.GetFiles(dir, "Wow*.exe"))
            .Where(IsOfficial)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A name for an official version from its folder: _classic_era_ becomes "WoW Classic Era".</summary>
    public static string VersionName(string exePath)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(exePath))!.Trim('_').Replace('_', ' ');
        return "WoW " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(folder);
    }

    public static BattleNetInstall? Find(string exePath)
    {
        if (LauncherFor(exePath) is not { } launcher)
            return null;

        var versionFolder = Path.GetFileName(Path.GetDirectoryName(exePath)!);
        return new BattleNetInstall(launcher, ReadProductCode(Path.Combine(Path.GetDirectoryName(launcher)!, ".product.db"), versionFolder));
    }

    private static string? LauncherFor(string exePath)
    {
        var versionDir = Path.GetDirectoryName(exePath);
        var root = string.IsNullOrEmpty(versionDir) ? null : Path.GetDirectoryName(versionDir);
        if (string.IsNullOrEmpty(root))
            return null;

        var launcher = Path.Combine(root, "World of Warcraft Launcher.exe");
        return File.Exists(launcher) ? launcher : null;
    }

    /// <summary>Opens Battle.net on this game version and starts it, the same way Blizzard's Start Menu shortcuts do.</summary>
    public void Launch()
    {
        var psi = new ProcessStartInfo(LauncherPath) { UseShellExecute = false };
        if (ProductCode != null)
            psi.ArgumentList.Add($"--productcode={ProductCode}");
        Process.Start(psi);
    }

    // .product.db is a protobuf list of installs. Each record names its product (wow_classic_era) before its install
    // folder (_classic_era_), so scanning the strings in order pairs them without a protobuf parser.
    private static string? ReadProductCode(string productDb, string versionFolder)
    {
        if (!File.Exists(productDb))
            return null;

        string? product = null;
        foreach (Match match in ProductOrFolder.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(productDb))))
        {
            if (match.Value.StartsWith("wow"))
                product = match.Value;
            else if (match.Value == versionFolder)
                return product;
        }
        return null;
    }
}
