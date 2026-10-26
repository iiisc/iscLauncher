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

    /// <summary>"1 addon", "3 addons".</summary>
    public static string Plural(this int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public static async Task<StorageFile?> PickFileAsync(IntPtr ownerHwnd, string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        return await picker.PickSingleFileAsync();
    }
}
