using System;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Dialogs;

/// <summary>
/// Confirmation dialog shown before pushing a full WTF snapshot to the sync repo.
/// </summary>
public static class SyncPushDialog
{
    /// <summary>
    /// Shows the push confirmation dialog. Returns <c>true</c> if the user confirmed.
    /// </summary>
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot)
    {
        var contentPanel = new StackPanel { Spacing = 12 };

        contentPanel.Children.Add(new TextBlock
        {
            Text = "This will upload a snapshot of your current addons and all character settings to the repo.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        contentPanel.Children.Add(new TextBlock
        {
            Text = "⚠ This will overwrite the repo with your current local addons and WTF settings.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = DialogHelper.CreateThemedDialog(xamlRoot, "⚔ Push to Repo");
        dialog.Content = contentPanel;
        dialog.PrimaryButtonText = "Push";

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
