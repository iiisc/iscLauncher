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
/// Picks the repo a game syncs with from the ones the user has given iscLauncher on GitHub, or walks them through
/// making one. The app can't create repos (that permission would also let it change their settings), so making the
/// repo and giving it to iscLauncher happen on GitHub, one step at a time; signing in is step 1, before this dialog.
/// </summary>
public static class SetUpSyncDialog
{
    /// <param name="listAsync">The repos iscLauncher can use, as name and clone URL, or null if it isn't installed on the account.</param>
    /// <returns>The chosen repo's clone URL, or null if the user cancelled.</returns>
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, string gameName, string installUrl,
        Func<Task<List<(string Name, string CloneUrl)>?>> listAsync)
    {
        List<(string Name, string CloneUrl)> repos = [];
        var installed = false;

        var pickText = BodyText("Pick the repo this game syncs with, such as the one it uses on another PC.");
        var repoBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Repo" };
        var refresh = StyledButton("Refresh");
        refresh.VerticalAlignment = VerticalAlignment.Bottom;
        var note = WarningText("");
        var picker = new Grid { ColumnSpacing = 8 };
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(refresh, 1);
        picker.Children.Add(repoBox);
        picker.Children.Add(refresh);
        var newRepo = Link("Set Up a New Repo Instead");
        var pick = Panel(10, pickText, picker, note, newRepo);

        var description = Uri.EscapeDataString("WoW addons and settings, synced by iscLauncher");
        var make = Panel(10,
            SectionLabel("STEP 2 OF 3 · MAKE A REPO"),
            BodyText("A repo is a private folder on GitHub that only you can see. iscLauncher keeps this game's addons " +
                "and settings in it, so your other PCs can pull them."),
            Link("Create the Repo on GitHub",
                $"https://github.com/new?name={SuggestedRepoName(gameName)}&visibility=private&description={description}"),
            Dim("GitHub fills everything in. Click Create repository there, then come back and click Next."));

        var giveSteps = BodyText("");
        var give = Panel(10,
            SectionLabel("STEP 3 OF 3 · GIVE IT TO ISCLAUNCHER"),
            BodyText("iscLauncher can only use the repos you give it, so your other projects on GitHub stay out of its reach."),
            giveSteps,
            Link("Open iscLauncher on GitHub", installUrl),
            Dim("Waiting for the repo to show up here..."));

        var dialog = CreateThemedDialog(xamlRoot, "Set Up Sync");
        // One width for every step, so the dialog doesn't jump around as the text changes
        SetWidth(dialog, 560, 560);

        // Repos that were there before the walk-through, so the one it made can be told apart
        HashSet<string> known = [];
        var view = pick;
        void Show(StackPanel next)
        {
            view = next;
            if (view == make)
                known = repos.Select(r => r.Name).ToHashSet();
            if (view == give)
                giveSteps.Text = installed
                    ? "On GitHub, click Configure next to your account, add the new repo under Repository access, and click Save."
                    : "On GitHub, choose your account if it asks where to install, pick Only select repositories, select the new repo, and click Install.";
            dialog.Content = view;
            dialog.PrimaryButtonText = view == pick ? "Use This Repo" : view == make ? "Next" : "";
            dialog.IsPrimaryButtonEnabled = view != pick || repoBox.SelectedIndex >= 0;
            dialog.SecondaryButtonText = view == give || (view == make && repos.Count > 0) ? "Back" : "";
        }

        var loading = false;
        async Task LoadAsync(bool quietly)
        {
            if (loading) return;
            loading = true;
            refresh.IsEnabled = false;
            if (!quietly)
                note.Text = "Looking up your repos on GitHub...";
            var picked = repoBox.SelectedItem as string;
            var failed = false;
            try
            {
                var found = await listAsync();
                installed = found != null;
                repos = found ?? [];
                note.Text = "";
            }
            catch (Exception ex)
            {
                note.Text = $"Couldn't look up your repos: {ex.Message}";
                failed = true;
            }
            var names = repos.Select(r => r.Name).ToList();
            if (!names.SequenceEqual(repoBox.ItemsSource as List<string> ?? []))
            {
                // A new repo from the walk-through is picked; otherwise a refresh keeps the user's pick
                var made = view != pick ? names.FirstOrDefault(n => !known.Contains(n)) : null;
                picked = made ?? picked;
                repoBox.ItemsSource = names;
                repoBox.SelectedIndex = picked != null && names.Contains(picked) ? names.IndexOf(picked) : names.Count > 0 ? 0 : -1;
                if (made != null)
                {
                    pickText.Text = $"{made} is ready. Click Use This Repo.";
                    Show(pick);
                }
            }
            if (!quietly && !failed && repos.Count == 0 && view == pick)
                Show(make);
            refresh.IsEnabled = true;
            loading = false;
        }

        repoBox.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = repoBox.SelectedIndex >= 0;
        refresh.Click += async (_, _) => await LoadAsync(quietly: false);
        newRepo.Click += (_, _) => Show(make);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (view != make) return;
            args.Cancel = true;
            Show(give);
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            Show(view == give ? make : pick);
        };

        // Repos are made and given to the app on github.com, so the list follows along while the user does that
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        poll.Tick += async (_, _) => await LoadAsync(quietly: true);
        dialog.Opened += async (_, _) =>
        {
            poll.Start();
            await LoadAsync(quietly: false);
        };

        Show(pick);
        var result = await dialog.ShowAsync();
        poll.Stop();
        if (result != ContentDialogResult.Primary || repoBox.SelectedIndex < 0)
            return null;
        return repos[repoBox.SelectedIndex].CloneUrl;
    }

    // GitHub repo names allow letters, digits, '-', '_' and '.'; the game name is folded into that
    private static string SuggestedRepoName(string gameName)
    {
        var slug = Regex.Replace(gameName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "wow-sync" : $"wow-sync-{slug}";
    }
}
