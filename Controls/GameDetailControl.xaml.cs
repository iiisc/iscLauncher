using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using Microsoft.UI.Xaml.Input;

namespace iscLauncher.Controls;

public sealed partial class GameDetailControl : UserControl
{
    public event EventHandler<GameEntry>? GameSaved;
    public event EventHandler? EscapeRequested;

    public GameRepository? GameRepository { get; set; }
    public CredentialService? CredentialService { get; set; }
    public GameLauncherService? GameLauncherService { get; set; }
    public AddonSyncService? AddonSyncService { get; set; }
    public GitHubService? GitHub { get; set; }
    public IntPtr OwnerHwnd { get; set; }
    public IReadOnlyCollection<GameEntry>? Games { get; set; }

    private GameEntry? _game;
    // A game being added: not in the library until saved
    private bool _isNew;
    // Games mid-launch or mid-password-entry; both take long enough that a second click would collide
    private readonly HashSet<Guid> _busyGames = new();
    private CancellationTokenSource? _syncCts;
    private bool _syncBusy;
    private readonly StatusNotifier _status;

    public GameEntry? CurrentGame => _game;

    public GameDetailControl()
    {
        InitializeComponent();
        _status = new StatusNotifier(StatusBar, StatusText);

        foreach (var box in new[] { NameTextBox, ExecutableTextBox, RealmlistTextBox, AccountTextBox, RealmTextBox, SyncRepoUrlTextBox })
            box.TextChanged += (_, _) => UpdateSaveButtons();
        PasswordBox.PasswordChanged += (_, _) => UpdateSaveButtons();
        StartupDelayNumberBox.ValueChanged += (_, _) => UpdateSaveButtons();
    }

    public void LoadGame(GameEntry game, string? statusMessage = null)
    {
        _game = game;
        _isNew = false;
        ShowOfficial(game.ExecutablePath);

        NameTextBox.Text = game.Name;
        ExecutableTextBox.Text = game.ExecutablePath;
        RealmlistTextBox.Text = game.RealmlistAddress ?? string.Empty;
        AccountTextBox.Text = game.AccountName ?? string.Empty;
        RealmTextBox.Text = game.RealmName ?? string.Empty;
        StartupDelayNumberBox.Value = game.StartupDelaySeconds;
        PasswordBox.Password = string.Empty;

        SyncRepoUrlTextBox.Text = game.SyncRepoUrl ?? string.Empty;
        UpdateLastSyncedText(game);

        DetailPivot.SelectedIndex = 0;
        UpdateLaunchButtonState(game);
        UpdateSaveButtons();

        if (statusMessage != null)
            ShowStatus(statusMessage, true);
        else
            StatusBar.IsOpen = false;
    }

    public void StartNewGame()
    {
        LoadGame(new GameEntry());
        _isNew = true;
        UpdateSaveButtons();
        NameTextBox.Focus(FocusState.Programmatic);
    }

    public async Task<bool> ConfirmNavigateAwayAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Unsaved Changes");
        dialog.Content = "You have unsaved changes that will be lost. Discard them?";
        dialog.PrimaryButtonText = "Discard";
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private int StartupDelay => double.IsNaN(StartupDelayNumberBox.Value) ? 0 : (int)StartupDelayNumberBox.Value;

    private bool HasUnsavedChanges => _game is { } game && (
        NameTextBox.Text.Trim() != game.Name
        || ExecutableTextBox.Text.Trim() != game.ExecutablePath
        || RealmlistTextBox.Text.NullIfBlank() != game.RealmlistAddress
        || AccountTextBox.Text.NullIfBlank() != game.AccountName
        || RealmTextBox.Text.NullIfBlank() != game.RealmName
        || SyncRepoUrlTextBox.Text.NullIfBlank() != game.SyncRepoUrl
        || StartupDelay != game.StartupDelaySeconds
        || PasswordBox.Password.Length > 0);

    // Save and Cancel replace Launch while there's something to save. The addon actions work from the saved settings,
    // so they wait too.
    private void UpdateSaveButtons()
    {
        var editing = _isNew || HasUnsavedChanges;
        LaunchButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditModeButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

        CreateRepoButton.Visibility = string.IsNullOrWhiteSpace(SyncRepoUrlTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        var hasRepo = _game?.HasSyncRepo == true;
        CopyAddonsSection.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        SyncButtonsPanel.Visibility = !editing && hasRepo ? Visibility.Visible : Visibility.Collapsed;
        NoRepoHint.Visibility = !editing && !hasRepo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (_isNew) EscapeRequested?.Invoke(this, EventArgs.Empty);
        else if (_game != null) LoadGame(_game);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_game == null) return false;

        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        { ShowStatus("Game name is required.", false); return false; }
        if (string.IsNullOrWhiteSpace(ExecutableTextBox.Text))
        { ShowStatus("Executable path is required.", false); return false; }

        var game = _game;
        var oldPath = game.ExecutablePath;

        game.Name = NameTextBox.Text.Trim();
        game.ExecutablePath = ExecutableTextBox.Text.Trim();
        game.RealmlistAddress = RealmlistTextBox.Text.NullIfBlank();
        game.AccountName = AccountTextBox.Text.NullIfBlank();
        game.RealmName = RealmTextBox.Text.NullIfBlank();
        game.StartupDelaySeconds = StartupDelay;
        game.SyncRepoUrl = SyncRepoUrlTextBox.Text.NullIfBlank();

        if (!string.IsNullOrEmpty(PasswordBox.Password))
            CredentialService!.SaveCredential(game.CredentialTarget, PasswordBox.Password);

        if (oldPath != game.ExecutablePath)
            IconExtractor.InvalidateCache(oldPath);

        if (_isNew)
            await GameRepository!.AddGameAsync(game);
        else
            await GameRepository!.UpdateGameAsync(game);
        game.NotifyChanged();

        GameSaved?.Invoke(this, game);
        return true;
    }

    /// <summary>Creates a private GitHub repo for this game, signing in first if needed, and saves the game with it.</summary>
    private async void OnCreateRepoClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        CreateRepoButton.IsEnabled = false;
        try
        {
            if (GitHub!.Token == null && await GitHubSignInDialog.ShowAsync(XamlRoot, GitHub) == null)
                return;

            var nameBox = new TextBox { Text = SuggestedRepoName(NameTextBox.Text), Style = DialogHelper.Res<Style>("InputStyle") };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(DialogHelper.BodyText("A private repo on your GitHub account will hold this game's addons and settings. " +
                "Use the same name on your other PCs to sync with it."));
            panel.Children.Add(nameBox);
            var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Create Sync Repo");
            dialog.Content = panel;
            dialog.PrimaryButtonText = "Create";
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
                return;

            ShowStatus("Creating the repo on GitHub...", true);
            SyncRepoUrlTextBox.Text = await GitHub.CreateRepoAsync(nameBox.Text.Trim(), CancellationToken.None);
            if (await SaveAsync())
                ShowStatus("Sync is set up. Push to upload this PC's addons and settings.", true);
        }
        catch (Exception ex) { ShowStatus($"Couldn't create the repo: {ex.Message}", false); }
        finally { CreateRepoButton.IsEnabled = true; }
    }

    // GitHub repo names allow letters, digits, '-', '_' and '.'; the game name is folded into that
    private static string SuggestedRepoName(string gameName)
    {
        var slug = Regex.Replace(gameName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "wow-sync" : $"wow-sync-{slug}";
    }

    private async void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var file = await FormHelpers.PickFileAsync(OwnerHwnd, ".exe");
        if (file == null) return;
        ExecutableTextBox.Text = file.Path;
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            NameTextBox.Text = Path.GetFileNameWithoutExtension(file.Name);
        ShowOfficial(file.Path);
    }

    // Battle.net handles login for official installs, so the login settings don't apply
    private void ShowOfficial(string exePath)
    {
        var official = BattleNetInstall.IsOfficial(exePath);
        OfficialNote.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        ConnectionSection.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
        AutomationTab.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // While there's something to save, Escape belongs to the Cancel button
        if (_isNew || HasUnsavedChanges) return;
        EscapeRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        if (CanSendPassword(game))
            await WhileBusyAsync(game, () => RunLauncherAsync($"Sending password to {game.Name}...",
                () => GameLauncherService!.SendPasswordToRunningGameAsync(game)));
        else
            await LaunchAsync(game);
    }

    /// <summary>
    /// Launches the game. One that's already running is left alone: typing the password into a running game is only
    /// done from the Send Password button, so a stray double-click can't send it into the game's chat.
    /// </summary>
    public async Task LaunchAsync(GameEntry game)
    {
        var launched = await WhileBusyAsync(game, async () =>
            await OfferPullBeforeLaunchAsync(game)
            && await RunLauncherAsync($"Launching {game.Name}...", () => GameLauncherService!.LaunchGameAsync(game)));
        if (launched)
            await AfterGameClosesAsync(game);
    }

    private async Task<bool> WhileBusyAsync(GameEntry game, Func<Task<bool>> work)
    {
        if (!_busyGames.Add(game.Id))
        { ShowStatus($"Still working on {game.Name}...", false); return false; }
        try
        {
            return await work();
        }
        finally
        {
            _busyGames.Remove(game.Id);
            if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
        }
    }

    private async Task<bool> RunLauncherAsync(string startMessage, Func<Task<LaunchResult>> action)
    {
        ShowStatus(startMessage, true);
        var result = await action();
        ShowStatus(result.Message, result.Success);
        return result.Success;
    }

    /// <summary>
    /// For a synced game, offers to pull first when another PC has pushed since this one last synced, so you don't play
    /// on old settings and later push them over the newer ones. Returns false if the user cancels the launch.
    /// </summary>
    private async Task<bool> OfferPullBeforeLaunchAsync(GameEntry game)
    {
        if (!game.HasSyncRepo || _syncBusy || GameLauncherService!.IsRunning(game))
            return true;

        GitCommitEntry? change;
        _syncCts?.Cancel(); _syncCts?.Dispose();
        // Being offline shouldn't hold up playing, so the check gives up quickly; Cancel skips it
        _syncCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SetSyncBusy(true);
        ShowStatus($"Checking {game.Name}'s repo for changes from your other PCs...", true);
        try
        {
            var session = await AddonSyncService!.OpenAsync(game, null, _syncCts.Token);
            change = await AddonSyncService.FindUnpulledChangeAsync(session, _syncCts.Token);
        }
        // No network, no git or a cancelled check: launch as if there were nothing to pull
        catch { return true; }
        finally { SetSyncBusy(false); }

        if (change == null)
            return true;

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Changes From Another PC");
        dialog.Content = $"The repo has a push this PC hasn't pulled: “{change.Message}” ({change.DisplayDate}). Pull it before playing?";
        dialog.PrimaryButtonText = "Pull First";
        dialog.SecondaryButtonText = "Launch Anyway";
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await PullAsync(game);
                return true;
            case ContentDialogResult.Secondary:
                return true;
            default:
                StatusBar.IsOpen = false;
                return false;
        }
    }

    /// <summary>Resets the Launch button when the game closes and, for a synced game, offers to push what changed while playing.</summary>
    private async Task AfterGameClosesAsync(GameEntry game)
    {
        // Battle.net can take a while to start an official game, or wait on an update first
        if (!await GameLauncherService!.WaitForExitAsync(game, TimeSpan.FromMinutes(10)))
            return;
        if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
        if (!game.HasSyncRepo)
            return;

        var reminder = $"{game.Name} closed. Push from the Addons tab to share your changes with your other PCs.";
        if (_syncBusy) { ShowStatus(reminder, true); return; }

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Push Your Changes?");
        dialog.Content = $"You closed {game.Name}. Push its addons and settings so your other PCs get them?";
        dialog.PrimaryButtonText = "Push";
        dialog.CloseButtonText = "Not Now";
        ContentDialogResult answer;
        // Only one dialog can be open at a time; if another is, the status message is the reminder
        try { answer = await dialog.ShowAsync(); }
        catch (Exception) { ShowStatus(reminder, true); return; }
        if (answer == ContentDialogResult.Primary)
            await PushAsync(game);
    }

    // Official games log in through Battle.net, so there's never a password to send
    private bool CanSendPassword(GameEntry game) =>
        !BattleNetInstall.IsOfficial(game.ExecutablePath) && GameLauncherService!.IsRunning(game);

    private void UpdateLaunchButtonState(GameEntry game)
    {
        var sendPassword = CanSendPassword(game);
        LaunchButtonText.Text = sendPassword ? "Send Password" : "Launch";
        LaunchButtonIcon.Text = sendPassword ? "🔑" : "⚔";
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

    private void ShowStatus(string message, bool isSuccess) => _status.Show(message, isSuccess);
}
