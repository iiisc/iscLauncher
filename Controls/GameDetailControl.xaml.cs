using System;
using System.Collections.Generic;
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
    public IntPtr OwnerHwnd { get; set; }
    public IReadOnlyCollection<GameEntry>? Games { get; set; }

    private GameEntry? _game;
    private bool _isEditing;
    private readonly Dictionary<Guid, int> _runningProcessIds = new();
    // Games mid-launch or mid-password-entry; both take long enough that a second click would collide
    private readonly HashSet<Guid> _busyGames = new();
    private CancellationTokenSource? _syncCts;
    private readonly StatusNotifier _status;

    public bool IsEditing => _isEditing;
    public GameEntry? CurrentGame => _game;

    public GameDetailControl()
    {
        InitializeComponent();
        _status = new StatusNotifier(StatusBar, StatusText);
    }

    public void LoadGame(GameEntry game, string? statusMessage = null)
    {
        _game = game;
        _isEditing = false;
        SetEditMode(false);

        NameTextBox.Text = game.Name;
        ExecutableTextBox.Text = game.ExecutablePath;
        RealmlistTextBox.Text = game.RealmlistAddress ?? string.Empty;
        AccountTextBox.Text = game.AccountName ?? string.Empty;
        RealmTextBox.Text = game.RealmName ?? string.Empty;
        WindowTitleTextBox.Text = game.WindowTitle ?? string.Empty;
        StartupDelayNumberBox.Value = game.StartupDelaySeconds;
        PasswordBox.Password = string.Empty;
        InputMethodComboBox.SelectedIndex = game.InputMethod switch
        {
            PasswordInputMethod.Clipboard => 1,
            _ => 0
        };

        SyncRepoUrlTextBox.Text = game.SyncRepoUrl ?? string.Empty;
        SyncBranchTextBox.Text = game.SyncBranch ?? string.Empty;
        UpdateLastSyncedText(game);

        DetailPivot.SelectedIndex = 0;
        UpdateLaunchButtonState(game);

        if (statusMessage != null)
            ShowStatus(statusMessage, true);
        else
            StatusBar.IsOpen = false;
    }

    public void SetComputerName(string name) => ComputerNameTextBox.Text = name;

    public async Task<bool> ConfirmNavigateAwayAsync()
    {
        if (!IsEditing) return true;
        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Unsaved Changes");
        dialog.Content = "You have unsaved changes that will be lost. Discard them?";
        dialog.PrimaryButtonText = "Discard";
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        _isEditing = false;
        return true;
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        _isEditing = true;
        SetEditMode(true);
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (_game != null) LoadGame(_game);
        _isEditing = false;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_isEditing || _game == null) return;

        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        { ShowStatus("Game name is required.", false); return; }
        if (string.IsNullOrWhiteSpace(ExecutableTextBox.Text))
        { ShowStatus("Executable path is required.", false); return; }

        var game = _game;
        var oldPath = game.ExecutablePath;

        game.Name = NameTextBox.Text.Trim();
        game.ExecutablePath = ExecutableTextBox.Text.Trim();
        game.RealmlistAddress = RealmlistTextBox.Text.NullIfBlank();
        game.AccountName = AccountTextBox.Text.NullIfBlank();
        game.RealmName = RealmTextBox.Text.NullIfBlank();
        game.WindowTitle = WindowTitleTextBox.Text.NullIfBlank();
        game.StartupDelaySeconds = double.IsNaN(StartupDelayNumberBox.Value)
            ? 0 : (int)StartupDelayNumberBox.Value;
        game.InputMethod = InputMethodComboBox.SelectedIndex switch
        {
            1 => PasswordInputMethod.Clipboard,
            _ => PasswordInputMethod.SendKeys
        };
        game.SyncRepoUrl = SyncRepoUrlTextBox.Text.NullIfBlank();
        game.SyncBranch = SyncBranchTextBox.Text.NullIfBlank();

        if (!string.IsNullOrEmpty(PasswordBox.Password))
            CredentialService!.SaveCredential(game.CredentialTarget, PasswordBox.Password);

        if (oldPath != game.ExecutablePath)
            IconExtractor.InvalidateCache(oldPath);

        await GameRepository!.UpdateGameAsync(game);
        game.NotifyChanged();

        _isEditing = false;
        GameSaved?.Invoke(this, game);
    }

    private async void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var file = await FormHelpers.PickFileAsync(OwnerHwnd, ".exe");
        if (file != null) ExecutableTextBox.Text = file.Path;
    }

    private void SetEditMode(bool isEditing)
    {
        NameTextBox.IsEnabled = isEditing;
        ExecutableTextBox.IsEnabled = isEditing;
        RealmlistTextBox.IsEnabled = isEditing;
        AccountTextBox.IsEnabled = isEditing;
        RealmTextBox.IsEnabled = isEditing;
        WindowTitleTextBox.IsEnabled = isEditing;
        PasswordBox.IsEnabled = isEditing;
        InputMethodComboBox.IsEnabled = isEditing;
        StartupDelayNumberBox.IsEnabled = isEditing;
        SyncRepoUrlTextBox.IsEnabled = isEditing;
        SyncBranchTextBox.IsEnabled = isEditing;
        ComputerNameTextBox.IsEnabled = isEditing;

        BrowseButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        GameNameEditSection.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
        ViewModeButtons.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        EditModeButtons.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;

        var hasRepo = _game?.HasSyncRepo == true;
        CopyAddonsSection.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        SyncButtonsPanel.Visibility = !isEditing && hasRepo ? Visibility.Visible : Visibility.Collapsed;
        NoRepoHint.Visibility = !isEditing && !hasRepo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditing) return;
        EscapeRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_game != null) await LaunchAsync(_game);
    }

    /// <summary>Launches the game, or types the password into it if it's already running.</summary>
    public async Task LaunchAsync(GameEntry game)
    {
        if (!_busyGames.Add(game.Id))
        { ShowStatus($"Still working on {game.Name}...", false); return; }
        try
        {
            if (_runningProcessIds.TryGetValue(game.Id, out var runningPid))
                await SendPasswordAsync(game, runningPid);
            else
                await StartGameAsync(game);
        }
        finally
        {
            _busyGames.Remove(game.Id);
        }
    }

    private async Task SendPasswordAsync(GameEntry game, int processId)
    {
        ShowStatus($"Sending password to {game.Name}...", true);
        var result = await GameLauncherService!.SendPasswordToRunningGameAsync(game, processId);
        ShowStatus(result.Message, result.Success);
    }

    private async Task StartGameAsync(GameEntry game)
    {
        ShowStatus($"Launching {game.Name}...", true);
        var result = await GameLauncherService!.LaunchGameAsync(game);
        ShowStatus(result.Message, result.Success);

        if (result.Success && result.ProcessId is int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                _runningProcessIds[game.Id] = pid;
                UpdateLaunchButtonState(game);
                // The only place running state is cleared; subscribe before enabling so a quick exit isn't missed
                process.Exited += (s, e) => DispatcherQueue.TryEnqueue(() =>
                {
                    _runningProcessIds.Remove(game.Id);
                    if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
                });
                process.EnableRaisingEvents = true;
            }
            catch { }
        }
    }

    private void UpdateLaunchButtonState(GameEntry game)
    {
        var isRunning = _runningProcessIds.ContainsKey(game.Id);
        LaunchButtonText.Text = isRunning ? "Send Password" : "Launch";
        LaunchButtonIcon.Text = isRunning ? "🔑" : "⚔";
    }

    private async void OnSyncAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        await RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await AddonSyncService!.OpenAsync(game, p, ct);
            var preview = await AddonSyncService.PreviewPullAsync(session, ct);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, "⚔ Pull from Repo", "Pull",
                "Choose what to bring onto this PC. Anything replaced or removed is backed up first.",
                null, preview, toRepo: false);
            return choice == null ? null : await AddonSyncService.PullAsync(session, choice.Addons, choice.Options, p, ct);
        });
    }

    private async void OnUploadAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        await RunSyncAsync("Checking repo...", async (p, ct) =>
        {
            var session = await AddonSyncService!.OpenAsync(game, p, ct);
            var previewTask = AddonSyncService.PreviewPushAsync(session, ct);
            var warningTask = AddonSyncService.DescribeUnpulledChangesAsync(session, ct);
            await Task.WhenAll(previewTask, warningTask);
            var choice = await SyncPreviewDialog.ShowAsync(XamlRoot, "⚔ Push to Repo", "Push",
                "Choose what to upload from this PC. The repo keeps every earlier version, so you can roll back.",
                await warningTask, await previewTask, toRepo: true);
            return choice == null ? null : await AddonSyncService.PushAsync(session, choice.Addons, choice.Options, p, ct);
        });
    }

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
            return choice == null ? null : await AddonSyncService.RollbackAsync(session, commit, choice.Addons, choice.Options, p, ct);
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
        if (choice == null) return;
        await RunSyncAsync($"Copying addons to {choice.Target.Name}...", async (_, ct) =>
            await AddonSyncService!.CopyToGameAsync(game, choice.Target, choice.Addons, choice.CopySettings, ct));
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
        SyncAddonsButton.IsEnabled = !busy;
        UploadAddonsButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        CopyAddonsButton.IsEnabled = !busy;
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

    private async void OnComputerNameLostFocus(object sender, RoutedEventArgs e)
    {
        var name = ComputerNameTextBox.Text?.Trim() ?? "";
        if (name.Length == 0) ComputerNameTextBox.Text = Environment.MachineName;
        try { await GameRepository!.SetComputerNameAsync(name); } catch { }
    }

    private void ShowStatus(string message, bool isSuccess) => _status.Show(message, isSuccess);
}
