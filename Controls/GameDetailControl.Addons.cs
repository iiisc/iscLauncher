using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Controls;

// The Addons tab
public sealed partial class GameDetailControl
{
    /// <summary>
    /// Links this game to a private GitHub repo, signing in first if needed: one the user picks (e.g. the repo this game
    /// already syncs with on another PC) or a new one. Saves the game with it.
    /// </summary>
    private async void OnSetUpSyncClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        SetUpSyncButton.IsEnabled = false;
        try
        {
            if (GitHub!.Token == null && await GitHubSignInDialog.ShowAsync(XamlRoot, GitHub) == null)
                return;

            ShowStatus("Looking up your repos on GitHub...", true);
            var repos = await GitHub.ListPrivateReposAsync(CancellationToken.None);
            StatusBar.IsOpen = false;

            var nameBox = new ComboBox
            {
                IsEditable = true,
                ItemsSource = repos.Select(r => r.Name).ToList(),
                Text = SuggestedRepoName(NameTextBox.Text),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(DialogHelper.BodyText("Pick the repo this game already syncs with on another PC, or type a name " +
                "to create a new private repo on your GitHub account."));
            panel.Children.Add(nameBox);
            var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Set Up Sync");
            dialog.Content = panel;
            dialog.PrimaryButtonText = "Use This Repo";
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
                return;

            var name = nameBox.Text.Trim();
            var isNew = !repos.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            ShowStatus(isNew ? "Creating the repo on GitHub..." : "Linking the repo...", true);
            SyncRepoUrlTextBox.Text = await GitHub.CreateRepoAsync(name, CancellationToken.None);
            if (await SaveAsync())
                ShowStatus(isNew
                    ? "Sync is set up. Push to upload this PC's addons and settings."
                    : "Sync is set up. Pull to bring this game's addons and settings onto this PC.", true);
        }
        catch (Exception ex) { ShowStatus($"Couldn't set up sync: {ex.Message}", false); }
        finally { SetUpSyncButton.IsEnabled = true; }
    }

    // GitHub repo names allow letters, digits, '-', '_' and '.'; the game name is folded into that
    private static string SuggestedRepoName(string gameName)
    {
        var slug = Regex.Replace(gameName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "wow-sync" : $"wow-sync-{slug}";
    }

    private async void OnSyncAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is { } game) await PullAsync(game);
    }

    private async void OnUploadAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is { } game) await PushAsync(game);
    }

    private Task PullAsync(GameEntry game) =>
        RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await AddonSyncService!.OpenAsync(game, p, ct);
            var preview = await AddonSyncService.PreviewPullAsync(session, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Pull {game.Name} from Repo", "Pull",
                "Choose what to bring onto this PC. Anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            if (choice == null || !await ConfirmGamesClosedAsync([game])) return null;
            return await AddonSyncService.PullAsync(session, choice.Addons, choice.Options, p, ct);
        });

    private Task PushAsync(GameEntry game) =>
        RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await AddonSyncService!.OpenAsync(game, p, ct);
            var previewTask = AddonSyncService.PreviewPushAsync(session, ct);
            var warningTask = AddonSyncService.DescribeUnpulledChangesAsync(session, ct);
            await Task.WhenAll(previewTask, warningTask);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Push {game.Name} to Repo", "Push",
                "Choose what to upload from this PC. The repo keeps every earlier version, so you can roll back.",
                await warningTask, await previewTask, toRepo: true);
            return choice == null ? null : await AddonSyncService.PushAsync(session, choice.Addons, choice.Options, p, ct);
        });

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        await RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await AddonSyncService!.OpenAsync(game, p, ct);
            var commit = await RollbackDialog.ShowAsync(XamlRoot, () => AddonSyncService.GetCommitLogAsync(session, ct));
            if (commit == null) return null;

            var preview = await AddonSyncService.PreviewRollbackAsync(session, commit.Hash, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Roll Back to {commit.Hash}", "Roll Back",
                $"The repo goes back to “{commit.Message}” for every PC. Choose what to apply to this PC; anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            if (choice == null || !await ConfirmGamesClosedAsync([game])) return null;
            return await AddonSyncService.RollbackAsync(session, commit, choice.Addons, choice.Options, p, ct);
        });
    }

    private async void OnCopyAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var targets = Games?.Where(g => g.Id != game.Id).ToList() ?? [];
        if (targets.Count == 0)
        { ShowStatus("Add another game to your library to copy addons into it.", false); return; }

        var choice = await CopyAddonsDialog.ShowAsync(XamlRoot, game, targets,
            (target, ct) => AddonSyncService!.PreviewCopyAsync(game, target, ct));
        if (choice == null || !await ConfirmGamesClosedAsync([choice.Target])) return;
        await RunSyncAsync($"Copying addons to {choice.Target.Name}...", async (_, ct) =>
            await AddonSyncService!.CopyToGameAsync(game, choice.Target, choice.Addons, choice.CopySettings, ct));
    }

    private async void OnCopyCharacterSetupClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var sources = AddonSyncService!.ListCharacters(game);
        if (sources.Count == 0)
        { ShowStatus("No characters found for this game yet. Log in once so the game creates its settings folders.", false); return; }

        var targets = sources.Concat((Games ?? []).Where(g => g.Id != game.Id).SelectMany(AddonSyncService.ListCharacters)).ToList();
        var choice = await CharacterSetupDialog.ShowAsync(XamlRoot, sources, targets);
        if (choice == null || !await ConfirmGamesClosedAsync(choice.Targets.Select(t => t.Game).Distinct())) return;
        await RunSyncAsync("Copying character setup...", async (_, ct) =>
            await AddonSyncService.CopyCharacterSetupAsync(choice.Source, choice.Targets, choice.EnabledAddons, choice.AddonData, ct));
    }

    // WoW writes addon settings and AddOns.txt back when you log out, which would undo files changed while it runs
    private async Task<bool> ConfirmGamesClosedAsync(IEnumerable<GameEntry> games)
    {
        var running = games.Where(GameLauncherService!.IsRunning).Select(g => g.Name).ToList();
        if (running.Count == 0) return true;

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Game Is Running");
        dialog.Content = $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running. WoW saves addon settings when you log out, " +
            "which may overwrite what you're about to change. Close the game first, or continue anyway?";
        dialog.PrimaryButtonText = "Continue Anyway";
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Runs an addon operation with the busy UI. The operation returns a success message, or null if the user backed out.</summary>
    private async Task RunSyncAsync(string startMessage,
        Func<IProgress<string>, CancellationToken, Task<string?>> operation)
    {
        _syncCts?.Cancel(); _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;
        SetSyncBusy(true);
        ShowStatus(startMessage, true);
        try
        {
            var message = await operation(new Progress<string>(m => ShowStatus(m, true)), ct);
            if (message != null) ShowStatus(message, true);
            else StatusBar.IsOpen = false;
            if (_game != null) UpdateLastSyncedText(_game);
        }
        catch (OperationCanceledException) { ShowStatus("Cancelled.", false); }
        catch (SyncException ex) { ShowStatus(ex.Message, false); }
        // Addon and settings files are often locked by a running game or antivirus; report it rather than crash
        catch (Exception ex) { ShowStatus($"Failed: {ex.Message}", false); }
        finally { SetSyncBusy(false); }
    }

    private void OnCancelSyncClick(object sender, RoutedEventArgs e) => _syncCts?.Cancel();

    public void CancelSync() => _syncCts?.Cancel();

    private void OnOpenRepoClick(object sender, RoutedEventArgs e)
    {
        if (_game?.HasSyncRepo != true) return;
        var url = _game.SyncRepoUrl!.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { ShowStatus("Could not open browser.", false); }
    }

    private void SetSyncBusy(bool busy)
    {
        _syncBusy = busy;
        SyncAddonsButton.IsEnabled = !busy;
        UploadAddonsButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        CopyAddonsButton.IsEnabled = !busy;
        CopyCharacterSetupButton.IsEnabled = !busy;
        SyncProgressRing.IsActive = busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLastSyncedText(GameEntry game)
    {
        if (game.LastSynced is DateTime ts)
        {
            var e = DateTime.UtcNow - ts;
            LastSyncedText.Text = $"Last synced: {(e.TotalMinutes < 1 ? "just now" :
                e.TotalMinutes < 60 ? $"{(int)e.TotalMinutes} min ago" :
                e.TotalHours < 24 ? $"{(int)e.TotalHours}h ago" :
                $"{(int)e.TotalDays}d ago")}";
            LastSyncedText.Visibility = Visibility.Visible;
        }
        else { LastSyncedText.Visibility = Visibility.Collapsed; }
    }
}
