using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace iscLauncher.Helpers;

public static class FormHelpers
{
    public static string? NullIfBlank(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static async Task<StorageFile?> PickFileAsync(IntPtr ownerHwnd, string extension, PickerLocationId? startLocation = null)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        if (startLocation is { } location)
            picker.SuggestedStartLocation = location;
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        return await picker.PickSingleFileAsync();
    }
}
