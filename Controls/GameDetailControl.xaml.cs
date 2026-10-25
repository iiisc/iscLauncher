using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private GameEntry? _game;
    private bool _isEditing;
    private readonly Dictionary<Guid, int> _runningProcessIds = new();
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

        SyncAddonsButton.IsEnabled = !isEditing;
        UploadAddonsButton.IsEnabled = !isEditing;
        RollbackButton.IsEnabled = !isEditing;
        SyncButtonsPanel.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        AddonSyncTab.Visibility = isEditing ? Visibility.Visible :
            (_game?.HasSyncRepo == true ? Visibility.Visible : Visibility.Collapsed);
        OpenRepoButton.Visibility = isEditing ? Visibility.Collapsed :
            (_game?.HasSyncRepo == true ? Visibility.Visible : Visibility.Collapsed);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditing) return;
        EscapeRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        if (_runningProcessIds.TryGetValue(_game.Id, out var pid))
            await SendPasswordAsync(_game, pid);
        else
            await LaunchAsync(_game);
    }

    private async Task SendPasswordAsync(GameEntry game, int processId)
    {
        ShowStatus($"Sending password to {game.Name}...", true);
        var result = await GameLauncherService!.SendPasswordToRunningGameAsync(game, processId);
        ShowStatus(result.Message, result.Success);
        if (!result.Success)
        {
            // Process is gone — clean up running state
            _runningProcessIds.Remove(game.Id);
            if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
        }
    }

    public async Task LaunchAsync(GameEntry game)
    {
        if (_runningProcessIds.ContainsKey(game.Id))
        { ShowStatus($"{game.Name} is already running.", false); return; }

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
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) => DispatcherQueue.TryEnqueue(() =>
                {
                    _runningProcessIds.Remove(game.Id);
                    if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
                });
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
        if (_game == null) return;
        ShowStatus("Fetching addon list from repo...", true);
        List<string> repoAddons;
        try { repoAddons = await AddonSyncService!.GetRepoAddonListAsync(_game, new Progress<string>(m => ShowStatus(m, true))); }
        catch { repoAddons = []; }

        var charCount = AddonSyncService!.GetRepoCharacterCount(_game);
        var localAddons = AddonSyncService!.GetLocalAddonList(_game);
        if (await SyncPullDialog.ShowAsync(XamlRoot, repoAddons, localAddons, charCount) != ContentDialogResult.Primary) return;

        await RunSyncAsync("Syncing addons...",
            (p, ct) => AddonSyncService.SyncAsync(_game, p, ct));
    }

    private async void OnUploadAddonsClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        if (!await SyncPushDialog.ShowAsync(XamlRoot)) return;
        await RunSyncAsync("Uploading to repo...",
            (p, ct) => AddonSyncService!.UploadAsync(_game, p, ct));
    }

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (_game == null) return;
        var commit = await RollbackDialog.ShowAsync(XamlRoot, () => AddonSyncService!.GetCommitLogAsync(_game));
        if (commit == null) return;
        await RunSyncAsync("Rolling back...",
            (p, ct) => AddonSyncService!.RollbackAsync(_game, commit.Hash, commit.Message, commit.Body, p, ct));
    }

    private async Task RunSyncAsync(string startMessage,
        Func<IProgress<string>, CancellationToken, Task<SyncResult>> operation)
    {
        _syncCts?.Cancel(); _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;
        SetSyncBusy(true);
        ShowStatus(startMessage, true);
        try
        {
            var result = await operation(new Progress<string>(m => ShowStatus(m, true)), ct);
            ShowStatus(result.Message, result.Success);
            if (_game != null) UpdateLastSyncedText(_game);
        }
        catch (OperationCanceledException) { ShowStatus("Cancelled.", false); }
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
        SyncProgressRing.IsActive = busy;
        SyncProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelSyncButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
