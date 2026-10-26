using System.Diagnostics;

namespace iscLauncher.Helpers;

public static class Shell
{
    /// <summary>Opens a link, file or folder in whatever Windows uses for it. False if that failed.</summary>
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
