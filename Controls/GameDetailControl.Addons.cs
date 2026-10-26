using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
            if (_services.GitHub.Token == null && await GitHubSignInDialog.ShowAsync(XamlRoot, _services.GitHub) == null)
                return;

            ShowInfo("Looking up your repos on GitHub...");
            var repos = await _services.GitHub.ListPrivateReposAsync(CancellationToken.None);
            StatusBar.IsOpen = false;

            var name = await SetUpSyncDialog.ShowAsync(XamlRoot, repos.Select(r => r.Name).ToList(), NameTextBox.Text);
            if (name == null)
                return;

            var isNew = !repos.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            ShowInfo(isNew ? "Creating the repo on GitHub..." : "Linking the repo...");
            SyncRepoUrlTextBox.Text = await _services.GitHub.CreateRepoAsync(name, CancellationToken.None);
            if (await SaveAsync())
                ShowStatus(isNew
                    ? "Sync is set up. Push to upload this PC's addons and settings."
                    : "Sync is set up. Pull to bring this game's addons and settings onto this PC.", true);
        }
        catch (Exception ex) { ShowStatus($"Couldn't set up sync: {ex.Message}", false); }
        finally { SetUpSyncButton.IsEnabled = true; }
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
            var session = await _services.Sync.OpenAsync(game, p, ct);
            var preview = await _services.Sync.PreviewPullAsync(session, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Pull {game.Name} from Repo", "Pull",
                "Choose what to bring onto this PC. Anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            if (choice == null || !await ConfirmGamesClosedAsync([game])) return null;
            return await _services.Sync.PullAsync(session, choice.Addons, choice.Options, p, ct);
        });

    private Task PushAsync(GameEntry game) =>
        RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await _services.Sync.OpenAsync(game, p, ct);
            var previewTask = _services.Sync.PreviewPushAsync(session, ct);
            var warningTask = _services.Sync.DescribeUnpulledChangesAsync(session, ct);
            await Task.WhenAll(previewTask, warningTask);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Push {game.Name} to Repo", "Push",
                "Choose what to upload from this PC. The repo keeps every earlier version, so you can roll back.",
                await warningTask, await previewTask, toRepo: true);
            return choice == null ? null : await _services.Sync.PushAsync(session, choice.Addons, choice.Options, p, ct);
        });

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        await RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await _services.Sync.OpenAsync(game, p, ct);
            var commit = await RollbackDialog.ShowAsync(XamlRoot, () => _services.Sync.GetCommitLogAsync(session, ct));
            if (commit == null) return null;

            var preview = await _services.Sync.PreviewRollbackAsync(session, commit.Hash, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Roll Back to {commit.Hash}", "Roll Back",
                $"The repo goes back to “{commit.Message}” for every PC. Choose what to apply to this PC; anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            if (choice == null || !await ConfirmGamesClosedAsync([game])) return null;
            return await _services.Sync.RollbackAsync(session, commit, choice.Addons, choice.Options, p, ct);
        });
    }

    private async void OnCopyAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var targets = _services.Library.Games.Where(g => g.Id != game.Id).ToList();
        if (targets.Count == 0)
        { ShowStatus("Add another game to your library to copy addons into it.", false); return; }

        var choice = await CopyAddonsDialog.ShowAsync(XamlRoot, game, targets,
            (target, ct) => _services.Sync.PreviewCopyAsync(game, target, ct));
        if (choice == null || !await ConfirmGamesClosedAsync([choice.Target])) return;
        await RunSyncAsync($"Copying addons to {choice.Target.Name}...", async (_, ct) =>
            await _services.Sync.CopyToGameAsync(game, choice.Target, choice.Addons, choice.CopySettings, ct));
    }

    private async void OnCopyCharacterSetupClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var sources = _services.Sync.ListCharacters(game);
        if (sources.Count == 0)
        { ShowStatus("No characters found for this game yet. Log in once so the game creates its settings folders.", false); return; }

        var targets = sources.Concat(_services.Library.Games.Where(g => g.Id != game.Id).SelectMany(_services.Sync.ListCharacters)).ToList();
        var choice = await CharacterSetupDialog.ShowAsync(XamlRoot, sources, targets);
        if (choice == null || !await ConfirmGamesClosedAsync(choice.Targets.Select(t => t.Game).Distinct())) return;
        await RunSyncAsync("Copying character setup...", async (_, ct) =>
            await _services.Sync.CopyCharacterSetupAsync(choice.Source, choice.Targets, choice.EnabledAddons, choice.AddonData, ct));
    }

    private async void OnCheckAddonUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        if (AddonUpdateService.Sources(game).Count == 0)
        { ShowStatus("None of this game's addons name a GitHub project. Add the repos to check under GitHub Sources.", false); return; }

        await RunSyncAsync("Checking for addon updates...", async (p, ct) =>
        {
            try
            {
                var updates = await _services.AddonUpdates.CheckAsync(game, p, ct);
                if (updates.All(u => u.Changed.Count == 0 && u.Error == null))
                    return $"Addons from {updates.Count.Plural("GitHub repo")} are up to date.";

                var picked = await AddonUpdatesDialog.ShowAsync(XamlRoot, game, updates);
                if (picked is not { Count: > 0 }) return null;
                return await _services.Sync.InstallUpdatesAsync(game, picked, ct);
            }
            finally { AddonUpdateService.DiscardDownloads(); }
        });
    }

    private async void OnAddonSourcesClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var sources = await AddonSourcesDialog.ShowAsync(XamlRoot, game.Name, AddonUpdateService.Sources(game));
        if (sources == null) return;
        game.AddonSources = sources;
        await _services.Library.SaveAsync();
        ShowStatus($"Updates will be looked for in {sources.Count.Plural("GitHub repo")}.", true);
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var backups = _services.Sync.ListBackups(game);
        if (backups.Count == 0)
        { ShowStatus($"There are no backups of {game.Name} yet.", false); return; }

        var backup = await RestoreBackupDialog.ShowAsync(XamlRoot, game.Name, backups);
        if (backup == null || !await ConfirmGamesClosedAsync([game]))
            return;

        await RunSyncAsync("Restoring the backup...", async (_, ct) => await _services.Sync.RestoreBackupAsync(game, backup, ct));
    }

    // WoW writes addon settings and AddOns.txt back when you log out, which would undo files changed while it runs
    private async Task<bool> ConfirmGamesClosedAsync(IEnumerable<GameEntry> games)
    {
        var running = games.Where(_services.Launcher.IsRunning).Select(g => g.Name).ToList();
        if (running.Count == 0) return true;

        return await DialogHelper.AskAsync(XamlRoot, "Game Is Running",
            $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running. WoW saves addon settings when you log out, " +
            "which may overwrite what you're about to change. Close the game first, or continue anyway?",
            "Continue Anyway") == ContentDialogResult.Primary;
    }

    /// <summary>Runs an addon operation with the busy UI. The operation returns a success message, or null if the user backed out.</summary>
    private async Task RunSyncAsync(string startMessage,
        Func<IProgress<string>, CancellationToken, Task<string?>> operation)
    {
        _syncCts?.Cancel(); _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;
        SetSyncBusy(true);
        ShowInfo(startMessage);
        try
        {
            var message = await operation(new Progress<string>(ShowInfo), ct);
            if (message != null) ShowStatus(message, true);
            else StatusBar.IsOpen = false;
            if (_game != null) UpdateSyncStatus(_game);
        }
        catch (OperationCanceledException) { ShowStatus("Cancelled.", false); }
        catch (AppException ex)
        {
            if (ex.InnerException != null) Log.Write(ex.InnerException.ToString());
            ShowStatus(ex.Message, false);
        }
        // Addon and settings files are often locked by a running game or antivirus; report it rather than crash
        catch (Exception ex)
        {
            Log.Write(ex.ToString());
            ShowStatus($"Failed: {ex.Message}", false);
        }
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
        RestoreBackupButton.IsEnabled = !busy;
        CheckAddonUpdatesButton.IsEnabled = !busy;
        AddonSourcesButton.IsEnabled = !busy;
        LaunchButton.IsEnabled = !busy;
        SyncProgressRing.IsActive = busy;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // The repo's head is checked in the background at startup and after every sync, so this can change while the game is shown
    private void OnGameChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => { if (sender is GameEntry game && game == _game) UpdateSyncStatus(game); });

    private void UpdateSyncStatus(GameEntry game)
    {
        SyncStatusPanel.Visibility = game.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed;
        var lastSynced = game.LastSynced is DateTime ts ? $"last synced {Ago(DateTime.UtcNow - ts)}" : "not synced from this PC yet";
        var (glyph, brush, text) =
            game.HasRemoteChanges ? ("", "GoldBrush", $"New from another PC: pull to get it ({lastSynced})")
            : game.RemoteHead != null ? ("", "EmeraldForegroundBrush", $"Up to date with the repo ({lastSynced})")
            : ("", "TextMutedBrush", char.ToUpper(lastSynced[0]) + lastSynced[1..]);
        SyncStatusIcon.Glyph = glyph;
        SyncStatusIcon.Foreground = DialogHelper.Res<Brush>(brush);
        SyncStatusText.Foreground = DialogHelper.Res<Brush>(game.HasRemoteChanges ? "GoldBrush" : "TextPrimaryBrush");
        SyncStatusText.Text = text;
        SyncAddonsButton.Style = DialogHelper.Res<Style>(game.HasRemoteChanges ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
    }

    private static string Ago(TimeSpan e) =>
        e.TotalMinutes < 1 ? "just now"
        : e.TotalMinutes < 60 ? $"{(int)e.TotalMinutes} min ago"
        : e.TotalHours < 24 ? $"{(int)e.TotalHours}h ago"
        : $"{(int)e.TotalDays}d ago";
}
