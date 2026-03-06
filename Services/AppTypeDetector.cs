using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace iscLauncher.Services;

public class AppTypeDetector
{
    // Known DirectX/OpenGL game indicators
    private static readonly string[] GameDllIndicators = 
    {
        "d3d9", "d3d10", "d3d11", "d3d12", "dxgi", "opengl32", "vulkan",
        "steam_api", "steamclient", "gameoverlayrenderer",
        "unityplayer", "unrealengine", "cryengine",
        "physx", "fmod", "wwise", "bink"
    };

    // Known Windows app frameworks
    private static readonly string[] WindowsAppIndicators =
    {
        "wpf", "presentationframework", "presentationcore", "windowsbase",
        "system.windows.forms", "devexpress", "telerik", "infragistics"
    };

    public enum AppType
    {
        Unknown,
        DirectXGame,
        WindowsApp
    }

    public record DetectionResult(AppType Type, string Reason, Models.PasswordInputMethod SuggestedMethod);

    public DetectionResult DetectAppType(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return new DetectionResult(AppType.Unknown, "File not found", Models.PasswordInputMethod.SendKeys);
        }

        try
        {
            var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(executablePath).ToLowerInvariant();

            // Check for game-related DLLs in the same directory
            var dllFiles = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
                .ToHashSet();

            // Check for DirectX/game indicators
            foreach (var indicator in GameDllIndicators)
            {
                if (dllFiles.Any(dll => dll.Contains(indicator)))
                {
                    return new DetectionResult(
                        AppType.DirectXGame, 
                        $"Found game library: {indicator}", 
                        Models.PasswordInputMethod.SendKeys);
                }
            }

            // Check for Windows app indicators
            foreach (var indicator in WindowsAppIndicators)
            {
                if (dllFiles.Any(dll => dll.Contains(indicator)))
                {
                    return new DetectionResult(
                        AppType.WindowsApp, 
                        $"Found Windows framework: {indicator}", 
                        Models.PasswordInputMethod.UIAutomation);
                }
            }

            // Check PE headers for subsystem type
            var peResult = CheckPEHeaders(executablePath);
            if (peResult != null)
            {
                return peResult;
            }

            // Check common game folder patterns
            var pathLower = executablePath.ToLowerInvariant();
            if (pathLower.Contains("steam") || 
                pathLower.Contains("games") || 
                pathLower.Contains("world of warcraft") ||
                pathLower.Contains("battle.net") ||
                pathLower.Contains("epic games") ||
                pathLower.Contains("origin") ||
                pathLower.Contains("ubisoft"))
            {
                return new DetectionResult(
                    AppType.DirectXGame, 
                    "Located in common game directory", 
                    Models.PasswordInputMethod.SendKeys);
            }

            // Check for .NET assembly (likely Windows app)
            if (dllFiles.Contains("system.runtime") || 
                dllFiles.Contains("coreclr") ||
                dllFiles.Contains("clrjit"))
            {
                return new DetectionResult(
                    AppType.WindowsApp, 
                    "Detected .NET application", 
                    Models.PasswordInputMethod.UIAutomation);
            }

            // Default to SendKeys as it works for most games
            return new DetectionResult(
                AppType.Unknown, 
                "Could not determine app type - defaulting to SendKeys", 
                Models.PasswordInputMethod.SendKeys);
        }
        catch (Exception ex)
        {
            return new DetectionResult(
                AppType.Unknown, 
                $"Detection error: {ex.Message}", 
                Models.PasswordInputMethod.SendKeys);
        }
    }

    private DetectionResult? CheckPEHeaders(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream);

            if (peReader.HasMetadata)
            {
                // .NET assembly - likely a Windows app
                return new DetectionResult(
                    AppType.WindowsApp, 
                    "Detected .NET managed executable", 
                    Models.PasswordInputMethod.UIAutomation);
            }

            // Check imported DLLs from PE headers
            // Native apps without .NET metadata are likely games if they're large
            var fileSize = new FileInfo(executablePath).Length;
            if (fileSize > 50 * 1024 * 1024) // > 50MB suggests game
            {
                return new DetectionResult(
                    AppType.DirectXGame, 
                    "Large native executable (likely game)", 
                    Models.PasswordInputMethod.SendKeys);
            }
        }
        catch
        {
            // PE parsing failed, continue with other checks
        }

        return null;
    }
}
