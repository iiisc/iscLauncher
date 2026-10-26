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

    public static async Task<StorageFile?> PickFileAsync(IntPtr ownerHwnd, string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        return await picker.PickSingleFileAsync();
    }
}
