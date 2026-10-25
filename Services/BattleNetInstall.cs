using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace iscLauncher.Services;

/// <summary>
/// An official Blizzard install: one folder per game version (_classic_era_, _forever_, ...) under a root holding
/// Blizzard's launcher. These log in through Battle.net, so they're started through it instead of with a password.
/// </summary>
public record BattleNetInstall(string LauncherPath, string? ProductCode)
{
    private static readonly Regex ProductOrFolder = new(@"wow[a-z_]*|_[a-z_]+_");

    public static BattleNetInstall? Find(string exePath)
    {
        var versionDir = Path.GetDirectoryName(exePath);
        var root = string.IsNullOrEmpty(versionDir) ? null : Path.GetDirectoryName(versionDir);
        if (string.IsNullOrEmpty(root))
            return null;

        var launcher = Path.Combine(root, "World of Warcraft Launcher.exe");
        if (!File.Exists(launcher))
            return null;

        return new BattleNetInstall(launcher, ReadProductCode(Path.Combine(root, ".product.db"), Path.GetFileName(versionDir!)));
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
