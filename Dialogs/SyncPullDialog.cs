using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Dialogs;

/// <summary>
/// Confirmation dialog shown before pulling addons from the sync repo.
/// Displays the addon list and a character-overwrite warning.
/// </summary>
public static class SyncPullDialog
{
    public static async Task<ContentDialogResult> ShowAsync(
        XamlRoot xamlRoot,
        List<string> repoAddons,
        int characterCount)
    {
        var warningText = characterCount > 0
            ? $"This will sync addons and restore {characterCount} character(s) from the repo snapshot. Continue?"
            : "This will sync addons from the repo. Continue?";

        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = warningText,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        if (characterCount > 0)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = "⚠ Local character settings will be overwritten for characters present in the repo snapshot.",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (repoAddons.Count > 0)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = $"📦 INCLUDED ADDONS ({repoAddons.Count})",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
                FontSize = 10,
                CharacterSpacing = 100,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldDarkBrush"],
                Margin = new Thickness(0, 4, 0, 0)
            });

            var addonListText = new TextBlock
            {
                Text = string.Join(",  ", repoAddons),
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap
            };

            contentPanel.Children.Add(new ScrollViewer
            {
                MaxHeight = 120,
                Content = addonListText
            });
        }

        var dialog = DialogHelper.CreateThemedDialog(xamlRoot, "⚔ Pull from Repo");
        dialog.Content = contentPanel;
        dialog.PrimaryButtonText = "Pull";
        dialog.MinWidth = 500;
        dialog.MaxWidth = 600;
        dialog.Resources["ContentDialogMaxWidth"] = 600.0;
        dialog.Resources["ContentDialogMinWidth"] = 500.0;

        return await dialog.ShowAsync();
    }
}
