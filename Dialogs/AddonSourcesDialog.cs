using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>Edits the GitHub repos a game's addon updates come from, one link per line.</summary>
public static class AddonSourcesDialog
{
    /// <returns>The repos as "owner/name", or null if the user cancelled.</returns>
    public static async Task<List<string>?> ShowAsync(XamlRoot xamlRoot, string gameName, List<string> sources)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 140,
            MaxHeight = 260,
            Style = Res<Style>("InputStyle"),
            PlaceholderText = "https://github.com/owner/repo",
            // TextBox separates lines with \r
            Text = string.Join("\r", sources.Select(s => $"https://github.com/{s}"))
        };
        AutomationProperties.SetName(box, "GitHub repos");
        var error = WarningText("");
        error.Visibility = Visibility.Collapsed;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BodyText($"Updates for {gameName} are looked for in these GitHub repos, one per line. " +
            "A repo can hold one addon or several; only the ones you have installed are updated."));
        panel.Children.Add(Dim("To add an addon, find its project on GitHub (searching for its name and your game version, " +
            "such as 3.3.5, usually turns it up) and paste the link."));
        panel.Children.Add(box);
        panel.Children.Add(error);

        var dialog = CreateThemedDialog(xamlRoot, "GitHub Sources");
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Save";
        SetWidth(dialog, 560, 680);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (Lines(box.Text).FirstOrDefault(l => AddonFiles.GitHubRepo(l) == null) is { } bad)
            {
                args.Cancel = true;
                error.Text = $"⚠ Not a GitHub repo: {bad}";
                error.Visibility = Visibility.Visible;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return Lines(box.Text).Select(l => AddonFiles.GitHubRepo(l)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
