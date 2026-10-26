using System;
using System.IO;
using iscLauncher.Services;

namespace iscLauncher.Helpers;

/// <summary>A small rolling log in the app data folder, so a failed sync or login can be looked into afterwards.</summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(GameRepository.AppDataFolder, "log.txt");

    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(GameRepository.AppDataFolder);
                // Once full, keep the newer half, so the log never grows without bound
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines[(lines.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        // Logging must never be what makes something fail
        catch { }
    }
}
