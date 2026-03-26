using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace iscLauncher;

public sealed partial class MainWindow : Window
{
    private readonly GameRepository _gameRepository = new();
    private readonly CredentialService _credentialService = new();
    private readonly GameLauncherService _gameLauncherService;
    private readonly AddonSyncService _addonSyncService = new(new GitService());
    private readonly ObservableCollection<GameEntry> _games = new();
    private readonly HashSet<Guid> _runningGames = new();
    private GameEntry? _currentEditingGame;
    private CancellationTokenSource? _syncCts;

    public MainWindow()
    {
        InitializeComponent();
        _gameLauncherService = new GameLauncherService(_credentialService);
        GameListView.ItemsSource = _games;

        Closed += (_, _) => _gameLauncherService.CancelPendingClipboardClear();

        // Hook into ListView loaded event to update selection visuals
        GameListView.Loaded += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                UpdateSelectionVisuals();
            });
        };

        // Set window size and custom title bar
        SetWindowSize(900, 650);
        SetupCustomTitleBar();

        _ = LoadGamesAsync();
    }

    private void SetWindowSize(int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        // Guard against null AppWindow (can happen in some host scenarios)
        if (appWindow != null)
        {
            // Scale to current DPI so the window looks the same on high-DPI laptops
            var dpi = GetDpiForWindow(hwnd);
            var scalingFactor = dpi / 96.0;
            var scaledWidth = (int)(width * scalingFactor);
            var scaledHeight = (int)(height * scalingFactor);

            appWindow.Resize(new Windows.Graphics.SizeInt32(scaledWidth, scaledHeight));

            // Set a minimum size so the Launch button is always visible
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                // OverlappedPresenter doesn't expose MinWidth directly;
                // we enforce via the Win32 minimum tracking size below.
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetupCustomTitleBar()
    {
        // Extend content into title bar
        ExtendsContentIntoTitleBar = true;
        // AppTitleBar may be null depending on XAML loading; guard to avoid NRE
        if (AppTitleBar != null)
        {
            SetTitleBar(AppTitleBar);
        }
    }

    private async Task LoadGamesAsync()
    {
        try
        {
            var library = await _gameRepository.LoadAsync();
            _games.Clear();
            foreach (var game in library.Games)
            {
                _games.Add(game);
            }

            ComputerNameTextBox.Text = string.IsNullOrWhiteSpace(library.ComputerName)
                ? Environment.MachineName
                : library.ComputerName;

            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to load games: {ex.Message}", false);
        }
    }

    private void UpdateEmptyState()
    {
        // Update empty state in the right panel
        bool hasGames = _games.Count > 0;

        // If no game is selected and we have games, auto-select first game
        if (hasGames && GameListView.SelectedItem == null && _games.Count > 0)
        {
            GameListView.SelectedIndex = 0;

            // Manually trigger visual update for the selected item
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateSelectionVisuals();
            });
        }
        else if (!hasGames)
        {
            // Show empty state in detail panel
            HideGameDetails();
        }
    }

    private void UpdateSelectionVisuals()
    {
        // Update visual states for all items
        foreach (var item in GameListView.Items)
        {
            var container = GameListView.ContainerFromItem(item) as ListViewItem;
            if (container != null)
            {
                var border = FindVisualChild<Border>(container, "GameCardBorder");
                if (border != null)
                {
                    bool isSelected = item == GameListView.SelectedItem;

                    // Update border appearance
                    if (isSelected)
                    {
                        border.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"];
                        border.BorderThickness = new Thickness(2);
                        border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface3Brush"];
                        border.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                        border.Translation = new System.Numerics.Vector3(0, 0, 8);
                    }
                    else
                    {
                        border.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderBrush"];
                        border.BorderThickness = new Thickness(1);
                        border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface2Brush"];
                        border.Shadow = null;
                        border.Translation = new System.Numerics.Vector3(0, 0, 0);
                    }
                }
            }
        }
    }

    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionVisuals();

        if (GameListView.SelectedItem is GameEntry game)
        {
            ShowGameDetails(game);
        }
        else
        {
            HideGameDetails();
        }
    }

    private T? FindVisualChild<T>(DependencyObject parent, string name = "") where T : DependencyObject
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);

            if (child is T typedChild && (string.IsNullOrEmpty(name) || (child as FrameworkElement)?.Name == name))
            {
                return typedChild;
            }

            var result = FindVisualChild<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private void ShowGameDetails(GameEntry game)
    {
        // Show detail panel, hide empty state
        DetailPanel.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        // Reset edit mode
        _currentEditingGame = null;
        SetEditMode(false);

        // Populate details
        DetailGameName.Text = game.Name;
        EditGameNameTextBox.Text = game.Name;
        EditExecutableTextBox.Text = game.ExecutablePath;
        EditRealmlistTextBox.Text = game.RealmlistAddress ?? string.Empty;
        EditAccountTextBox.Text = game.AccountName ?? string.Empty;
        EditRealmTextBox.Text = game.RealmName ?? string.Empty;
        EditWindowTitleTextBox.Text = game.WindowTitle ?? string.Empty;
        EditStartupDelayNumberBox.Value = game.StartupDelaySeconds;
        EditPasswordBox.Password = string.Empty;

        EditInputMethodComboBox.SelectedIndex = game.InputMethod switch
        {
            PasswordInputMethod.SendKeys => 0,
            PasswordInputMethod.Clipboard => 1,
            _ => 0
        };

        // Store selected game in button tags
        DetailEditButton.Tag = game;
        DetailLaunchButton.Tag = game;
        SaveEditButton.Tag = game;

        // Populate sync fields
        EditSyncRepoUrlTextBox.Text = game.SyncRepoUrl ?? string.Empty;
        EditSyncBranchTextBox.Text = game.SyncBranch ?? string.Empty;
        AddonSyncPivotItem.Visibility = game.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed;
        OpenRepoLink.Visibility = game.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed;
        UpdateLastSyncedText(game);

        // Reset to General tab when switching games
        DetailPivot.SelectedIndex = 0;

        // Reflect running state on the launch button
        UpdateLaunchButtonState(game);
    }

    private void HideGameDetails()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    private void SetEditMode(bool isEditing)
    {
        // Toggle controls interactive state
        EditGameNameTextBox.IsEnabled = isEditing;
        EditRealmlistTextBox.IsEnabled = isEditing;
        EditAccountTextBox.IsEnabled = isEditing;
        EditRealmTextBox.IsEnabled = isEditing;
        EditWindowTitleTextBox.IsEnabled = isEditing;
        EditPasswordBox.IsEnabled = isEditing;
        EditInputMethodComboBox.IsEnabled = isEditing;
        EditStartupDelayNumberBox.IsEnabled = isEditing;

        // Executable: enabled but readonly, use browse button to change
        EditExecutableTextBox.IsEnabled = isEditing;
        EditExecutableTextBox.IsReadOnly = true;

        // Toggle browse button
        BrowseButton.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;

        // Toggle Game Name edit section
        GameNameEditSection.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;

        // Update header text visibility
        DetailGameName.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;

        // Toggle button groups
        ViewModeButtons.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        EditModeButtons.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;

        // Addon sync controls
        EditSyncRepoUrlTextBox.IsEnabled = isEditing;
        EditSyncBranchTextBox.IsEnabled = isEditing;
        SyncAddonsButton.IsEnabled = !isEditing;
        UploadAddonsButton.IsEnabled = !isEditing;
        RollbackButton.IsEnabled = !isEditing;
        AddonSyncPivotItem.Visibility = isEditing ? Visibility.Visible :
            (GameListView.SelectedItem is GameEntry g && g.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed);
        SyncButtonsPanel.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        OpenRepoLink.Visibility = isEditing ? Visibility.Collapsed :
            (GameListView.SelectedItem is GameEntry g2 && g2.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed);
    }

    private void OnDetailPivotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Placeholder for any tab-switch logic if needed later
    }

    private async void OnAddGameClick(object sender, RoutedEventArgs e)
    {
        var dialog = new GameDialog(this);
        dialog.XamlRoot = Content.XamlRoot;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.GameEntry != null)
        {
            // Save password to credential manager
            if (!string.IsNullOrEmpty(dialog.Password))
            {
                _credentialService.SaveCredential(dialog.GameEntry.CredentialTarget, dialog.Password);
            }

            // Save game to repository
            await _gameRepository.AddGameAsync(dialog.GameEntry);
            _games.Add(dialog.GameEntry);
            UpdateEmptyState();

            ShowStatus("Game added successfully!", true);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is GameEntry game)
        {
            var confirmDialog = DialogHelper.CreateThemedDialog(Content.XamlRoot, "Delete Game");
            confirmDialog.Content = $"Are you sure you want to delete '{game.Name}'? This will also remove the stored password.";
            confirmDialog.PrimaryButtonText = "Delete";

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _credentialService.DeleteCredential(game.CredentialTarget);
                await _gameRepository.RemoveGameAsync(game.Id);
                _games.Remove(game);

                GameListView.SelectedItem = null;
                UpdateEmptyState();

                ShowStatus("Game deleted.", true);
            }
        }
    }

    private void OnDetailEditClick(object sender, RoutedEventArgs e)
    {
        if (GameListView.SelectedItem is GameEntry game)
        {
            _currentEditingGame = game;
            SetEditMode(true);
        }
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        // Return to view mode
        if (_currentEditingGame != null && GameListView.SelectedItem is GameEntry game)
        {
            ShowGameDetails(game);
        }
        _currentEditingGame = null;
    }

    private async void OnSaveEditClick(object sender, RoutedEventArgs e)
    {
        if (_currentEditingGame == null) return;

        // Validate
        if (string.IsNullOrWhiteSpace(EditGameNameTextBox.Text))
        {
            ShowStatus("Game name is required", false);
            return;
        }

        if (string.IsNullOrWhiteSpace(EditExecutableTextBox.Text))
        {
            ShowStatus("Executable path is required", false);
            return;
        }

        var oldPath = _currentEditingGame.ExecutablePath;
        var gameId = _currentEditingGame.Id; // Store the ID before async operations

        // Update game entry
        _currentEditingGame.Name = EditGameNameTextBox.Text.Trim();
        _currentEditingGame.ExecutablePath = EditExecutableTextBox.Text.Trim();
        _currentEditingGame.RealmlistAddress = EditRealmlistTextBox.Text.Trim();
        _currentEditingGame.AccountName = EditAccountTextBox.Text.Trim();
        _currentEditingGame.RealmName = EditRealmTextBox.Text.Trim();
        _currentEditingGame.WindowTitle = EditWindowTitleTextBox.Text.Trim();
        _currentEditingGame.StartupDelaySeconds = double.IsNaN(EditStartupDelayNumberBox.Value)
            ? 0 : (int)EditStartupDelayNumberBox.Value;

        _currentEditingGame.InputMethod = EditInputMethodComboBox.SelectedIndex switch
        {
            0 => PasswordInputMethod.SendKeys,
            1 => PasswordInputMethod.Clipboard,
            _ => PasswordInputMethod.SendKeys
        };

        // Update sync fields
        _currentEditingGame.SyncRepoUrl = string.IsNullOrWhiteSpace(EditSyncRepoUrlTextBox.Text)
            ? null : EditSyncRepoUrlTextBox.Text.Trim();
        _currentEditingGame.SyncBranch = string.IsNullOrWhiteSpace(EditSyncBranchTextBox.Text)
            ? null : EditSyncBranchTextBox.Text.Trim();

        // Update password if provided
        if (!string.IsNullOrEmpty(EditPasswordBox.Password))
        {
            _credentialService.SaveCredential(_currentEditingGame.CredentialTarget, EditPasswordBox.Password);
        }

        // Invalidate icon cache if executable path changed
        if (oldPath != _currentEditingGame.ExecutablePath)
        {
            Services.IconExtractor.InvalidateCache(oldPath);
        }

        // Update game in repository
        await _gameRepository.UpdateGameAsync(_currentEditingGame);

        // Refresh list and show updated details
        await LoadGamesAsync();
        var updatedGame = _games.FirstOrDefault(g => g.Id == gameId); // Use stored ID
        if (updatedGame != null)
        {
            GameListView.SelectedItem = updatedGame;
            ShowGameDetails(updatedGame);
        }

        _currentEditingGame = null;
        ShowStatus("Game updated successfully!", true);
    }

    private async void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");

        var hwnd = WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            EditExecutableTextBox.Text = file.Path;
        }
    }

    private async void OnDetailLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is GameEntry game)
        {
            await LaunchGameAsync(game);
        }
    }

    private async void OnGameDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GameListView.SelectedItem is GameEntry game)
        {
            await LaunchGameAsync(game);
        }
    }

    private async Task LaunchGameAsync(GameEntry game)
    {
        // Prevent launching the same game entry twice
        if (_runningGames.Contains(game.Id))
        {
            ShowStatus($"{game.Name} is already running.", false);
            return;
        }

        // Show immediate feedback
        ShowStatus($"Launching {game.Name}...", true);

        var result = await _gameLauncherService.LaunchGameAsync(game);
        ShowStatus(result.Message, result.Success);

        // Track the launched process so this entry can't be launched again until it exits
        if (result.Success && result.ProcessId is int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                _runningGames.Add(game.Id);
                UpdateLaunchButtonState(game);

                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _runningGames.Remove(game.Id);
                        // Update button if this game is still selected
                        if (GameListView.SelectedItem is GameEntry selected && selected.Id == game.Id)
                        {
                            UpdateLaunchButtonState(selected);
                        }
                    });
                };
            }
            catch
            {
                // Process already exited before we could attach; don't track it
            }
        }
    }

    private void UpdateLaunchButtonState(GameEntry game)
    {
        var isRunning = _runningGames.Contains(game.Id);
        DetailLaunchButton.IsEnabled = !isRunning;
        LaunchButtonText.Text = isRunning ? "Running" : "Launch";
    }

    private CancellationTokenSource? _statusHideCts;

    private void ShowStatus(string message, bool isSuccess)
    {
        // Cancel and dispose any previous auto-hide timer
        _statusHideCts?.Cancel();
        _statusHideCts?.Dispose();
        _statusHideCts = null;

        StatusText.Text = message;
        StatusInfoBar.Severity = isSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusInfoBar.IsOpen = true;

        if (isSuccess)
        {
            var cts = new CancellationTokenSource();
            _statusHideCts = cts;
            _ = HideStatusAfterDelay(5000, cts.Token);
        }
    }

    private async Task HideStatusAfterDelay(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            StatusInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // Timer was cancelled by a newer status message
        }
    }

    private async void OnGameIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Image image && 
            image.DataContext is GameEntry game)
        {
            var icon = await Services.IconExtractor.GetIconFromExecutableAsync(game.ExecutablePath);
            if (icon != null)
            {
                image.Source = icon;

                // Hide the fallback icon since we have a real icon
                if (image.Parent is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is FontIcon fallbackIcon)
                        {
                            fallbackIcon.Visibility = Visibility.Collapsed;
                            break;
                        }
                    }
                }
            }
        }
    }

    private void UpdateLastSyncedText(GameEntry game)
    {
        if (game.LastSynced is DateTime lastSynced)
        {
            var elapsed = DateTime.UtcNow - lastSynced;
            var text = elapsed.TotalMinutes < 1 ? "just now" :
                       elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes} min ago" :
                       elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago" :
                       $"{(int)elapsed.TotalDays}d ago";
            LastSyncedText.Text = $"Last synced: {text}";
            LastSyncedText.Visibility = Visibility.Visible;
        }
        else
        {
            LastSyncedText.Visibility = Visibility.Collapsed;
        }
    }

    private void SetSyncUiBusy(bool busy)
    {
        SyncAddonsButton.IsEnabled = !busy;
        UploadAddonsButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        SyncProgressRing.IsActive = busy;
        SyncProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelSyncButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSyncAddonsClick(object sender, RoutedEventArgs e)
    {
        if (GameListView.SelectedItem is not GameEntry game) return;

        // Fetch repo addon list (this also ensures the cache is up to date)
        ShowStatus("Fetching addon list from repo...", true);
        List<string> repoAddons;
        try
        {
            var fetchProgress = new Progress<string>(msg => ShowStatus(msg, true));
            repoAddons = await _addonSyncService.GetRepoAddonListAsync(game, fetchProgress);
        }
        catch
        {
            repoAddons = [];
        }

        // Count characters that will be overwritten
        var gameDir = System.IO.Path.GetDirectoryName(game.ExecutablePath);
        var charCount = 0;
        if (!string.IsNullOrEmpty(gameDir))
        {
            var wtfAccountDir = System.IO.Path.Combine(gameDir, "WTF", "Account");
            charCount = AddonSyncService.EnumerateCharacterFolders(wtfAccountDir).Count();
        }

        if (await SyncPullDialog.ShowAsync(Content.XamlRoot, repoAddons, charCount) != ContentDialogResult.Primary)
            return;

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        SetSyncUiBusy(true);
        ShowStatus("Syncing addons...", true);

        try
        {
            var progress = new Progress<string>(msg => ShowStatus(msg, true));
            var result = await _addonSyncService.SyncAsync(game, progress, ct);
            ShowStatus(result.Message, result.Success);
            UpdateLastSyncedText(game);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Sync cancelled.", false);
        }
        finally
        {
            SetSyncUiBusy(false);
        }
    }

    private async void OnUploadAddonsClick(object sender, RoutedEventArgs e)
    {
        if (GameListView.SelectedItem is not GameEntry game) return;

        var gameDir = System.IO.Path.GetDirectoryName(game.ExecutablePath);
        if (string.IsNullOrEmpty(gameDir)) return;

        var wtfAccountDir = System.IO.Path.Combine(gameDir, "WTF", "Account");
        var characters = AddonSyncService.EnumerateCharacterFolders(wtfAccountDir).ToList();

        if (characters.Count == 0)
        {
            ShowStatus("No character folders found under WTF/Account/.", false);
            return;
        }

        var characterPath = await SyncPushDialog.ShowAsync(Content.XamlRoot, wtfAccountDir, characters);
        if (string.IsNullOrEmpty(characterPath))
            return;

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        SetSyncUiBusy(true);
        ShowStatus("Uploading to repo...", true);

        try
        {
            var progress = new Progress<string>(msg => ShowStatus(msg, true));
            var result = await _addonSyncService.UploadAsync(game, characterPath, progress, ct);
            ShowStatus(result.Message, result.Success);
            UpdateLastSyncedText(game);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Upload cancelled.", false);
        }
        finally
        {
            SetSyncUiBusy(false);
        }
    }

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (GameListView.SelectedItem is not GameEntry game) return;

        var selectedCommit = await RollbackDialog.ShowAsync(
            Content.XamlRoot,
            () => _addonSyncService.GetCommitLogAsync(game));
        if (selectedCommit == null)
            return;

        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        SetSyncUiBusy(true);
        ShowStatus("Rolling back...", true);

        try
        {
            var progress = new Progress<string>(msg => ShowStatus(msg, true));
            var result = await _addonSyncService.RollbackAsync(game, selectedCommit.Hash, selectedCommit.Message, selectedCommit.Body, progress, ct);
            ShowStatus(result.Message, result.Success);
            UpdateLastSyncedText(game);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Rollback cancelled.", false);
        }
        finally
        {
            SetSyncUiBusy(false);
        }
    }

    private void OnCancelSyncClick(object sender, RoutedEventArgs e)
    {
        _syncCts?.Cancel();
    }

    private void OnOpenRepoClick(object sender, RoutedEventArgs e)
    {
        if (GameListView.SelectedItem is not GameEntry game || !game.HasSyncRepo) return;

        var url = game.SyncRepoUrl!.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            ShowStatus("Could not open browser.", false);
        }
    }

    private async void OnComputerNameLostFocus(object sender, RoutedEventArgs e)
    {
        var name = ComputerNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ComputerNameTextBox.Text = Environment.MachineName;
            name = null; // clear the override so it falls back
        }
        try
        {
            await _gameRepository.SetComputerNameAsync(name ?? "");
        }
        catch
        {
            // Non-fatal — name will still be used from the TextBox next time
        }
    }
}
