using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>
/// Picks the repo a game syncs with from the ones the user has given iscLauncher on GitHub. The app can't create repos
/// (that permission would also let it change their settings), so a new one is made on GitHub, which this links to.
/// </summary>
public static class SetUpSyncDialog
{
    /// <param name="listAsync">The repos iscLauncher can use, as name and clone URL, or null if it isn't installed on the account.</param>
    /// <returns>The chosen repo's clone URL, or null if the user cancelled.</returns>
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, string gameName, string installUrl,
        Func<Task<List<(string Name, string CloneUrl)>?>> listAsync)
    {
        List<(string Name, string CloneUrl)> repos = [];
        var repoBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Repo" };
        var refresh = new Button { Content = "Refresh", Style = Res<Style>("SecondaryButtonStyle"), VerticalAlignment = VerticalAlignment.Bottom };
        var note = WarningText("");
        var picker = new Grid { ColumnSpacing = 8 };
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(refresh, 1);
        picker.Children.Add(repoBox);
        picker.Children.Add(refresh);

        var description = Uri.EscapeDataString("WoW addons and settings, synced by iscLauncher");
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        links.Children.Add(Link("Create a Repo on GitHub",
            $"https://github.com/new?name={SuggestedRepoName(gameName)}&visibility=private&description={description}"));
        links.Children.Add(Link("Choose iscLauncher's Repos", installUrl));

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BodyText("Pick the repo this game syncs with, such as the one it uses on another PC. " +
            "iscLauncher can only see the private repos you've given it on GitHub."));
        panel.Children.Add(picker);
        panel.Children.Add(note);
        panel.Children.Add(SectionLabel("A NEW REPO"));
        panel.Children.Add(BodyText("1. Create a private repo on GitHub.\n2. Add it to the repos iscLauncher can use.\n3. Pick it here once it shows up."));
        panel.Children.Add(links);

        var dialog = CreateThemedDialog(xamlRoot, "Set Up Sync");
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Use This Repo";
        dialog.IsPrimaryButtonEnabled = false;
        SetWidth(dialog, 520, 640);

        var loading = false;
        async Task LoadAsync(bool quietly)
        {
            if (loading) return;
            loading = true;
            refresh.IsEnabled = false;
            if (!quietly)
                note.Text = "Looking up your repos on GitHub...";
            var picked = repoBox.SelectedItem as string;
            try
            {
                var found = await listAsync();
                repos = found ?? [];
                note.Text = found == null ? "iscLauncher isn't installed on your GitHub account yet. Choose iscLauncher's repos below; this list updates by itself."
                    : repos.Count == 0 ? "iscLauncher hasn't been given any private repos yet."
                    : "";
            }
            catch (Exception ex)
            {
                note.Text = $"Couldn't look up your repos: {ex.Message}";
            }
            var names = repos.Select(r => r.Name).ToList();
            if (!names.SequenceEqual(repoBox.ItemsSource as List<string> ?? []))
            {
                repoBox.ItemsSource = names;
                // A refresh keeps the user's pick
                repoBox.SelectedIndex = picked != null && names.Contains(picked) ? names.IndexOf(picked) : names.Count > 0 ? 0 : -1;
            }
            refresh.IsEnabled = true;
            loading = false;
        }

        // Repos are created and given to the app on github.com, so the list follows along while the user does that
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        poll.Tick += async (_, _) => await LoadAsync(quietly: true);
        repoBox.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = repoBox.SelectedIndex >= 0;
        refresh.Click += async (_, _) => await LoadAsync(quietly: false);
        dialog.Opened += async (_, _) =>
        {
            poll.Start();
            await LoadAsync(quietly: false);
        };

        var result = await dialog.ShowAsync();
        poll.Stop();
        if (result != ContentDialogResult.Primary || repoBox.SelectedIndex < 0)
            return null;
        return repos[repoBox.SelectedIndex].CloneUrl;
    }

    private static HyperlinkButton Link(string text, string url) =>
        new() { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), Foreground = Res<Microsoft.UI.Xaml.Media.Brush>("GoldBrush") };

    // GitHub repo names allow letters, digits, '-', '_' and '.'; the game name is folded into that
    private static string SuggestedRepoName(string gameName)
    {
        var slug = Regex.Replace(gameName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "wow-sync" : $"wow-sync-{slug}";
    }
}
