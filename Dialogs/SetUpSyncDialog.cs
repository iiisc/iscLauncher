using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>Asks which GitHub repo a game syncs with: an existing one (e.g. the one it uses on another PC) or a new name.</summary>
public static class SetUpSyncDialog
{
    /// <returns>The repo name, or null if the user cancelled.</returns>
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, List<string> repoNames, string gameName)
    {
        var nameBox = new ComboBox
        {
            IsEditable = true,
            ItemsSource = repoNames,
            Text = SuggestedRepoName(gameName),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Header = "Repo name"
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BodyText("Pick the repo this game already syncs with on another PC, or type a name " +
            "to create a new private repo on your GitHub account."));
        panel.Children.Add(nameBox);
        var dialog = CreateThemedDialog(xamlRoot, "Set Up Sync");
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Use This Repo";
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
            return null;
        return nameBox.Text.Trim();
    }

    // GitHub repo names allow letters, digits, '-', '_' and '.'; the game name is folded into that
    private static string SuggestedRepoName(string gameName)
    {
        var slug = Regex.Replace(gameName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "wow-sync" : $"wow-sync-{slug}";
    }
}
