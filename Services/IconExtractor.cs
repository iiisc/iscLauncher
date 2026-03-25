using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    /// Gets the icon from an executable asynchronously, with caching for performance.
    /// The P/Invoke and bitmap work runs on a background thread; the BitmapImage is
    /// created on the calling (UI) thread.
    /// </summary>
    public static async Task<BitmapImage?> GetIconFromExecutableAsync(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            return null;

        if (_iconCache.TryGetValue(executablePath, out var cachedIcon))
            return cachedIcon;

        var bytes = await Task.Run(() => ExtractIconBytes(executablePath));
        if (bytes == null)
        {
            _iconCache.TryAdd(executablePath, null);
            return null;
        }

        var bitmapImage = new BitmapImage();
        using var memoryStream = new MemoryStream(bytes);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        bitmapImage.SetSource(randomAccessStream);

        _iconCache.TryAdd(executablePath, bitmapImage);
        return bitmapImage;
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

    private static byte[]? ExtractIconBytes(string executablePath)
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
            return memoryStream.ToArray();
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
