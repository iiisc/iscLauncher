using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace iscLauncher.Services;

public static class IconExtractor
{
    // Cache icons by executable path to avoid re-extracting
    private static readonly ConcurrentDictionary<string, BitmapImage?> _iconCache = new();

    /// <summary>Gets the executable's icon, cached by path. Returns null if Windows can't provide one.</summary>
    public static async Task<BitmapImage?> GetIconFromExecutableAsync(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            return null;

        if (_iconCache.TryGetValue(executablePath, out var cachedIcon))
            return cachedIcon;

        BitmapImage? icon;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(executablePath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 40);
            icon = new BitmapImage();
            await icon.SetSourceAsync(thumbnail);
        }
        catch
        {
            icon = null;
        }

        _iconCache[executablePath] = icon;
        return icon;
    }

    /// <summary>
    /// Removes a specific icon from the cache.
    /// </summary>
    public static void InvalidateCache(string executablePath)
    {
        _iconCache.TryRemove(executablePath, out _);
    }
}
