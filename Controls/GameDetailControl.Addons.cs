using System;
using System.ComponentModel;
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

// The Sync tab, and the Addons tab's list
public sealed partial class GameDetailControl
{
    /// <summary>
    /// Links this game to one of the private repos the user has given iscLauncher on GitHub, signing in first if
    /// needed, and saves the game with it.
    /// </summary>
    private async void OnSetUpSyncClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        SetUpSyncButton.IsEnabled = false;
        try
        {
            if (_services.GitHub.UsableToken == null
                && await GitHubSignInDialog.ShowAsync(XamlRoot, _services.GitHub, "STEP 1 OF 3 · SIGN IN") == null)
                return;

            var url = await SetUpSyncDialog.ShowAsync(XamlRoot, NameTextBox.Text, GitHubService.InstallUrl,
                () => _services.GitHub.ListSyncReposAsync(CancellationToken.None));
            if (url == null)
                return;
            SyncRepoUrlTextBox.Text = url;
            if (await SaveAsync())
                ShowStatus("Sync is set up. Push to upload this PC's addons and settings to a new repo, or pull to bring the repo's onto this PC." +
                    (await _services.Git.IsGitInstalledAsync(CancellationToken.None) ? ""
                        : $" Sync uses Git, which this PC doesn't have, so the first push or pull asks to download it ({GitService.MinGitDownloadMB} MB)."), true);
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
            if (await OpenSyncAsync(game, p, ct) is not { } session) return null;
            var preview = await _services.Sync.PreviewPullAsync(session, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Pull {game.Name} from Repo", "Pull",
                "Choose what to bring onto this PC. Anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            if (choice == null || !await ConfirmGameClosedAsync(game)) return null;
            return await _services.Sync.PullAsync(session, choice.Addons, choice.Options, p, ct);
        });

    private Task PushAsync(GameEntry game) =>
        RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            if (await OpenSyncAsync(game, p, ct) is not { } session) return null;
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
            if (await OpenSyncAsync(game, p, ct) is not { } session) return null;
            var commit = await RollbackDialog.ShowAsync(XamlRoot, () => _services.Sync.GetCommitLogAsync(session, ct));
            if (commit == null) return null;

            var preview = await _services.Sync.PreviewRollbackAsync(session, commit.Hash, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, $"⚔ Roll Back to {commit.Hash}", "Roll Back",
                $"The repo goes back to “{commit.Message}” for every PC. Choose what to apply to this PC; anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            if (choice == null || !await ConfirmGameClosedAsync(game)) return null;
            return await _services.Sync.RollbackAsync(session, commit, choice.Addons, choice.Options, p, ct);
        });
    }

    /// <summary>Opens the game's repo, first downloading Git if the PC has none and the user agrees. Null if they don't.</summary>
    private async Task<SyncSession?> OpenSyncAsync(GameEntry game, IProgress<string> progress, CancellationToken ct)
    {
        if (!await _services.Git.IsGitInstalledAsync(ct))
        {
            if (await DialogHelper.AskAsync(XamlRoot, "Download Git?",
                    "Sync keeps your addons and settings in your GitHub repo using Git, a free tool for keeping versions of files. " +
                    $"This PC doesn't have it, so iscLauncher needs to download it once: {GitService.MinGitName} from the Git for Windows " +
                    $"project on GitHub, about {GitService.MinGitDownloadMB} MB.\n\n" +
                    $"It's kept in {GitService.DownloadedGitDir} for iscLauncher alone. Nothing is installed in Windows, and deleting " +
                    "that folder removes it.",
                    "Download Git") != ContentDialogResult.Primary)
                return null;
            await _services.Git.DownloadGitAsync(new Progress<double>(p => progress.Report($"Downloading Git... {(int)(p * 100)}%")), ct);
        }
        return await _services.Sync.OpenAsync(game, progress, ct);
    }

    private async void OnCopyAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var targets = _services.Library.Games.Where(g => g.Id != game.Id).ToList();
        if (targets.Count == 0)
        { ShowStatus("Add another game to your library to copy addons into it.", false); return; }

        var choice = await CopyAddonsDialog.ShowAsync(XamlRoot, game, targets,
            (target, ct) => _services.Sync.PreviewCopyAsync(game, target, ct));
        if (choice == null || !await ConfirmGameClosedAsync(choice.Target)) return;
        await RunSyncAsync($"Copying addons to {choice.Target.Name}...", async (_, ct) =>
            await _services.Sync.CopyToGameAsync(game, choice.Target, choice.Addons, choice.CopySettings, ct));
    }

    // Listing a game's addons checks its GitHub repos, which means downloading them, so that waits until the tab is opened
    private void OnPivotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddonsTabShown && InstalledView == null && _game != null && !_isNew)
            ShowInstalledAddons(_game);
    }

    private bool AddonsTabShown => DetailPivot.SelectedItem as PivotItem == AddonsTab;

    private InstalledAddonsView? InstalledView => AddonsHost.Child as InstalledAddonsView;

    private void ShowInstalledAddons(GameEntry game)
    {
        var view = new InstalledAddonsView(game, _services.AddonUpdates)
        {
            IsBusy = _syncBusy,
            InstallAsync = async picked =>
            {
                // Pushing is how the update reaches the other PCs, so it's offered while it's on the user's mind
                if (await RunSyncAsync("Updating addons...", async (_, ct) => await _services.Sync.InstallUpdatesAsync(game, picked, ct))
                    && game.HasSyncRepo
                    && await DialogHelper.AskAsync(XamlRoot, "Push the Update?",
                        $"Push {game.Name}'s updated addons now, so your other PCs get them when they pull?", "Push Now", closeText: "Later")
                        == ContentDialogResult.Primary)
                    await PushAsync(game);
            }
        };
        view.SourcesChanged += async (_, _) => await _services.Library.SaveAsync();
        AddonsHost.Child = view;
        _ = view.LoadAsync();
    }

    /// <summary>Drops the Addons tab's list, so it's built afresh the next time the tab is opened.</summary>
    private void CloseInstalledAddons()
    {
        InstalledView?.Close();
        AddonsHost.Child = null;
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        var backups = _services.Sync.ListBackups(game);
        if (backups.Count == 0)
        { ShowStatus($"There are no backups of {game.Name} yet.", false); return; }

        var backup = await RestoreBackupDialog.ShowAsync(XamlRoot, game.Name, backups);
        if (backup == null || !await ConfirmGameClosedAsync(game))
            return;

        await RunSyncAsync("Restoring the backup...", async (_, ct) => await _services.Sync.RestoreBackupAsync(game, backup, ct));
    }

    // WoW writes addon settings and AddOns.txt back when you log out, which would undo files changed while it runs
    private async Task<bool> ConfirmGameClosedAsync(GameEntry game)
    {
        if (!_services.Launcher.IsRunning(game)) return true;

        return await DialogHelper.AskAsync(XamlRoot, "Game Is Running",
            $"{game.Name} is running. WoW saves addon settings when you log out, " +
            "which may overwrite what you're about to change. Close the game first, or continue anyway?",
            "Continue Anyway") == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Runs an addon operation with the busy UI. The operation returns a success message, or null if the user backed out.
    /// Returns whether it succeeded.
    /// </summary>
    private async Task<bool> RunSyncAsync(string startMessage,
        Func<IProgress<string>, CancellationToken, Task<string?>> operation)
    {
        var ct = StartSync(startMessage);
        try
        {
            var message = await operation(new Progress<string>(ShowInfo), ct);
            if (message != null) ShowStatus(message, true);
            else _status.Hide();
            if (_game != null) UpdateSyncStatus(_game);
            // Pulls, rollbacks, restores and updates change the addons the list shows
            if (InstalledView is { } view) await view.RescanAsync();
            return message != null;
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
        return false;
    }

    /// <summary>Takes over from any sync still running and marks the page busy; the caller ends with SetSyncBusy(false).</summary>
    private CancellationToken StartSync(string message, TimeSpan? timeout = null)
    {
        _syncCts?.Cancel(); _syncCts?.Dispose();
        _syncCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        SetSyncBusy(true);
        ShowInfo(message);
        return _syncCts.Token;
    }

    private void OnCancelSyncClick(object sender, RoutedEventArgs e) => _syncCts?.Cancel();

    public void CancelSync() => _syncCts?.Cancel();

    private void OnOpenRepoClick(object sender, RoutedEventArgs e)
    {
        if (_game?.HasSyncRepo != true) return;
        var url = _game.SyncRepoUrl!.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        if (!Shell.Open(url))
            ShowStatus("Could not open browser.", false);
    }

    private void SetSyncBusy(bool busy)
    {
        _syncBusy = busy;
        SyncAddonsButton.IsEnabled = !busy;
        UploadAddonsButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        CopyAddonsButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy;
        if (InstalledView is { } view) view.IsBusy = busy;
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
            _services.GitHub.SignInProblem is { } problem ? ("\uE7BA", "CrimsonBrush", $"{problem}. Sign in again under Options to sync.")
            : game.HasRemoteChanges ? ("", "GoldBrush", $"New from another PC: pull to get it ({lastSynced})")
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
