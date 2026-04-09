using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Dialogs;

/// <summary>
/// Confirmation dialog shown before pulling addons from the sync repo.
/// Displays addons categorised as new or updating, and a character-overwrite warning.
/// </summary>
public static class SyncPullDialog
{
    public static async Task<ContentDialogResult> ShowAsync(
        XamlRoot xamlRoot,
        List<string> repoAddons,
        List<string> localAddons,
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
            var localSet = new HashSet<string>(localAddons, StringComparer.OrdinalIgnoreCase);
            var newAddons = repoAddons.Where(a => !localSet.Contains(a)).ToList();
            var updatedAddons = repoAddons.Where(a => localSet.Contains(a)).ToList();

            contentPanel.Children.Add(new TextBlock
            {
                Text = $"📦 INCLUDED ADDONS ({repoAddons.Count})",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
                FontSize = 10,
                CharacterSpacing = 100,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldDarkBrush"],
                Margin = new Thickness(0, 4, 0, 0)
            });

            if (newAddons.Count > 0)
                contentPanel.Children.Add(BuildChangeSection(
                    $"✚  NEW ({newAddons.Count})",
                    newAddons,
                    (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EmeraldForegroundBrush"]));

            if (updatedAddons.Count > 0)
                contentPanel.Children.Add(BuildChangeSection(
                    $"↻  UPDATING ({updatedAddons.Count})",
                    updatedAddons,
                    (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"]));
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

    private static StackPanel BuildChangeSection(string label, List<string> addons, Microsoft.UI.Xaml.Media.Brush labelForeground)
    {
        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
            FontSize = 10,
            CharacterSpacing = 60,
            Foreground = labelForeground
        });
        section.Children.Add(new ScrollViewer
        {
            MaxHeight = 80,
            Content = new TextBlock
            {
                Text = string.Join(",  ", addons),
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextDimBrush"],
                TextWrapping = TextWrapping.Wrap
            }
        });
        return section;
    }
}
