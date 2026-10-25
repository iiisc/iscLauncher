using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

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
        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(BodyText(characterCount > 0
            ? $"This will sync addons and restore {characterCount} character(s) from the repo snapshot. Continue?"
            : "This will sync addons from the repo. Continue?"));

        if (characterCount > 0)
            contentPanel.Children.Add(WarningText(
                "⚠ Local character settings will be overwritten for characters present in the repo snapshot."));

        if (repoAddons.Count > 0)
        {
            var localSet = new HashSet<string>(localAddons, StringComparer.OrdinalIgnoreCase);
            var newAddons = repoAddons.Where(a => !localSet.Contains(a)).ToList();
            var updatedAddons = repoAddons.Where(a => localSet.Contains(a)).ToList();

            contentPanel.Children.Add(new TextBlock
            {
                Text = $"📦 INCLUDED ADDONS ({repoAddons.Count})",
                FontFamily = Res<FontFamily>("DisplayFont"),
                FontSize = 10,
                CharacterSpacing = 100,
                Foreground = Res<Brush>("GoldDarkBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });

            if (newAddons.Count > 0)
                contentPanel.Children.Add(BuildChangeSection(
                    $"✚  NEW ({newAddons.Count})", newAddons, Res<Brush>("EmeraldForegroundBrush")));

            if (updatedAddons.Count > 0)
                contentPanel.Children.Add(BuildChangeSection(
                    $"↻  UPDATING ({updatedAddons.Count})", updatedAddons, Res<Brush>("TextMutedBrush")));
        }

        var dialog = CreateThemedDialog(xamlRoot, "⚔ Pull from Repo");
        dialog.Content = contentPanel;
        dialog.PrimaryButtonText = "Pull";
        SetWidth(dialog, 500, 600);

        return await dialog.ShowAsync();
    }

    private static StackPanel BuildChangeSection(string label, List<string> addons, Brush labelForeground)
    {
        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = Res<FontFamily>("DisplayFont"),
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
                FontFamily = Res<FontFamily>("BodyFont"),
                FontSize = 11,
                Foreground = Res<Brush>("TextDimBrush"),
                TextWrapping = TextWrapping.Wrap
            }
        });
        return section;
    }
}
