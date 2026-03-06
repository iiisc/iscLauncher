using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace iscLauncher.Services;

public static class IconExtractor
{
    // Cache icons by executable path to avoid re-extracting
    private static readonly ConcurrentDictionary<string, BitmapImage?> _iconCache = new();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Gets the icon from an executable, with caching for performance.
    /// </summary>
    public static BitmapImage? GetIconFromExecutable(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            return null;

        // Check cache first
        if (_iconCache.TryGetValue(executablePath, out var cachedIcon))
        {
            return cachedIcon;
        }

        // Extract and cache
        var icon = ExtractIconInternal(executablePath);
        _iconCache.TryAdd(executablePath, icon);
        return icon;
    }

    /// <summary>
    /// Clears the icon cache (useful when a game's executable changes).
    /// </summary>
    public static void ClearCache()
    {
        _iconCache.Clear();
    }

    /// <summary>
    /// Removes a specific icon from the cache.
    /// </summary>
    public static void InvalidateCache(string executablePath)
    {
        _iconCache.TryRemove(executablePath, out _);
    }

    private static BitmapImage? ExtractIconInternal(string executablePath)
    {
        if (!File.Exists(executablePath))
            return null;

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            hIcon = ExtractIcon(IntPtr.Zero, executablePath, 0);
            if (hIcon == IntPtr.Zero || hIcon.ToInt64() == 1)
                return null;

            using var icon = Icon.FromHandle(hIcon);
            using var bitmap = icon.ToBitmap();
            using var memoryStream = new MemoryStream();

            bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
            memoryStream.Position = 0;

            var bitmapImage = new BitmapImage();
            var randomAccessStream = memoryStream.AsRandomAccessStream();
            bitmapImage.SetSource(randomAccessStream);

            return bitmapImage;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero && hIcon.ToInt64() != 1)
                DestroyIcon(hIcon);
        }
    }
}
