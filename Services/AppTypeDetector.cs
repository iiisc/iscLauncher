using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace iscLauncher.Services;

/// <summary>
/// Guesses what kind of app an executable is, to show the user why SendKeys was suggested.
/// </summary>
public class AppTypeDetector
{
    private static readonly string[] GameDllIndicators =
    {
        "d3d9", "d3d10", "d3d11", "d3d12", "dxgi", "opengl32", "vulkan",
        "steam_api", "steamclient", "gameoverlayrenderer",
        "unityplayer", "unrealengine", "cryengine",
        "physx", "fmod", "wwise", "bink"
    };

    private static readonly string[] WindowsAppIndicators =
    {
        "wpf", "presentationframework", "presentationcore", "windowsbase",
        "system.windows.forms", "devexpress", "telerik", "infragistics"
    };

    public string Describe(string executablePath)
    {
        if (!File.Exists(executablePath))
            return "File not found";

        try
        {
            var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            var dllFiles = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                .ToHashSet();

            var gameDll = GameDllIndicators.FirstOrDefault(i => dllFiles.Any(dll => dll.Contains(i)));
            if (gameDll != null)
                return $"Found game library: {gameDll}";

            var appDll = WindowsAppIndicators.FirstOrDefault(i => dllFiles.Any(dll => dll.Contains(i)));
            if (appDll != null)
                return $"Found Windows framework: {appDll}";

            var peResult = CheckPEHeaders(executablePath);
            if (peResult != null)
                return peResult;

            var pathLower = executablePath.ToLowerInvariant();
            if (pathLower.Contains("steam") ||
                pathLower.Contains("games") ||
                pathLower.Contains("world of warcraft") ||
                pathLower.Contains("battle.net") ||
                pathLower.Contains("epic games") ||
                pathLower.Contains("origin") ||
                pathLower.Contains("ubisoft"))
            {
                return "Located in common game directory";
            }

            if (dllFiles.Contains("system.runtime") ||
                dllFiles.Contains("coreclr") ||
                dllFiles.Contains("clrjit"))
            {
                return "Detected .NET application";
            }

            return "Could not determine app type - defaulting to SendKeys";
        }
        catch (Exception ex)
        {
            return $"Detection error: {ex.Message}";
        }
    }

    private static string? CheckPEHeaders(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream);

            if (peReader.HasMetadata)
                return "Detected .NET managed executable";

            // Native apps without .NET metadata are likely games if they're large
            if (new FileInfo(executablePath).Length > 50 * 1024 * 1024)
                return "Large native executable (likely game)";
        }
        catch
        {
            // PE parsing failed, continue with other checks
        }

        return null;
    }
}
