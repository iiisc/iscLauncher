using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>Lets the user pick one of a game's backups to put back.</summary>
public static class RestoreBackupDialog
{
    /// <returns>The chosen backup, or null if the user cancelled.</returns>
    public static async Task<BackupInfo?> ShowAsync(XamlRoot xamlRoot, string gameName, List<BackupInfo> backups)
    {
        var choices = new RadioButtons { ItemsSource = backups.Select(Describe).ToList(), SelectedIndex = 0 };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BodyText("Pick a backup to put back. What it replaces is backed up first, so this can be undone too."));
        panel.Children.Add(choices);
        var dialog = CreateThemedDialog(xamlRoot, $"Restore {gameName}");
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Restore";
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? backups[choices.SelectedIndex] : null;
    }

    private static string Describe(BackupInfo backup)
    {
        var parts = new List<string>();
        if (backup.HasSettings) parts.Add("settings");
        if (backup.Addons.Count > 0) parts.Add($"{backup.Addons.Count} addon(s)");
        return $"{backup.Created:g}: {string.Join(" and ", parts)}";
    }
}
