using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace iscLauncher.Services;

public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> _iconCache = new();

    // Two thumbnail requests at once on the UI thread make one fail with E_PENDING, so icons load one at a time
    private static readonly SemaphoreSlim _thumbnailLock = new(1, 1);

    /// <summary>Gets the executable's icon, cached by path. Returns null if Windows can't provide one.</summary>
    public static async Task<BitmapImage?> GetIconFromExecutableAsync(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            return null;

        if (_iconCache.TryGetValue(executablePath, out var cachedIcon))
            return cachedIcon;

        BitmapImage? icon;
        await _thumbnailLock.WaitAsync();
        try
        {
            if (_iconCache.TryGetValue(executablePath, out cachedIcon))
                return cachedIcon;
            var file = await StorageFile.GetFileFromPathAsync(executablePath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 40);
            icon = new BitmapImage();
            await icon.SetSourceAsync(thumbnail);
        }
        catch
        {
            icon = null;
        }
        finally
        {
            _thumbnailLock.Release();
        }

        _iconCache[executablePath] = icon;
        return icon;
    }

    public static void InvalidateCache(string executablePath)
    {
        _iconCache.TryRemove(executablePath, out _);
    }
}
