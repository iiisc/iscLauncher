using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
        AddonSyncSection.Visibility = game.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed;
        OpenRepoLink.Visibility = game.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed;
        UpdateLastSyncedText(game);

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
        AddonSyncSection.Visibility = isEditing ? Visibility.Visible :
            (GameListView.SelectedItem is GameEntry g && g.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed);
        SyncButtonsPanel.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
        OpenRepoLink.Visibility = isEditing ? Visibility.Collapsed :
            (GameListView.SelectedItem is GameEntry g2 && g2.HasSyncRepo ? Visibility.Visible : Visibility.Collapsed);
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
            var confirmDialog = new ContentDialog
            {
                Title = "Delete Game",
                Content = $"Are you sure you want to delete '{game.Name}'? This will also remove the stored password.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.None,
                XamlRoot = Content.XamlRoot,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface2Brush"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderHighBrush"],
                CornerRadius = new CornerRadius(8),
                RequestedTheme = ElementTheme.Dark
            };
            ApplyDialogTheme(confirmDialog);

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Delete credential
                _credentialService.DeleteCredential(game.CredentialTarget);

                // Remove from repository
                await _gameRepository.RemoveGameAsync(game.Id);
                _games.Remove(game);

                // Clear selection and update empty state
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

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (GameListView.SelectedItem is GameEntry game)
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

    private string _lastStatusMessage = string.Empty;
    private CancellationTokenSource? _statusHideCts;

    private void ShowStatus(string message, bool isSuccess)
    {
        _lastStatusMessage = message;

        // Cancel any previous auto-hide timer
        _statusHideCts?.Cancel();

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

        var warningText = charCount > 0
            ? $"This will overwrite addon and WTF settings for {charCount} character(s). Continue?"
            : "This will sync addons from the repo. Continue?";

        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = warningText,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        if (charCount > 0)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = "⚠ Local character settings will be replaced with the repo template.",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap
            });
        }

        // Show addon list
        if (repoAddons.Count > 0)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = $"📦 INCLUDED ADDONS ({repoAddons.Count})",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
                FontSize = 10,
                CharacterSpacing = 100,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldDarkBrush"],
                Margin = new Thickness(0, 4, 0, 0)
            });

            var addonListText = new TextBlock
            {
                Text = string.Join(",  ", repoAddons),
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap
            };

            contentPanel.Children.Add(new ScrollViewer
            {
                MaxHeight = 120,
                Content = addonListText
            });
        }

        var confirmDialog = new ContentDialog
        {
            Title = "⚔ Pull from Repo",
            Content = contentPanel,
            PrimaryButtonText = "Pull",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = Content.XamlRoot,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface2Brush"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderHighBrush"],
            CornerRadius = new CornerRadius(8),
            RequestedTheme = ElementTheme.Dark,
            MinWidth = 500,
            MaxWidth = 600
        };
        confirmDialog.Resources["ContentDialogMaxWidth"] = 600.0;
        confirmDialog.Resources["ContentDialogMinWidth"] = 500.0;
        ApplyDialogTheme(confirmDialog);

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _syncCts?.Cancel();
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

        // Build character picker ComboBox for the dialog
        var charComboBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceBrush"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Height = 40,
            FontSize = 12
        };
        foreach (var charPath in characters)
        {
            var relativePath = System.IO.Path.GetRelativePath(wtfAccountDir, charPath);
            charComboBox.Items.Add(new ComboBoxItem
            {
                Content = relativePath.Replace(System.IO.Path.DirectorySeparatorChar, '/'),
                Tag = charPath
            });
        }
        charComboBox.SelectedIndex = 0;

        // Build styled dialog content
        var contentPanel = new StackPanel { Spacing = 12 };

        contentPanel.Children.Add(new TextBlock
        {
            Text = "Select the character whose settings will become the template for all characters.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        // Character picker section
        var pickerSection = new StackPanel { Spacing = 4 };
        pickerSection.Children.Add(new TextBlock
        {
            Text = "Source Character",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 10,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"]
        });
        pickerSection.Children.Add(charComboBox);
        contentPanel.Children.Add(pickerSection);

        contentPanel.Children.Add(new TextBlock
        {
            Text = "⚠ This will overwrite the repo with your current local addons and the selected character's settings.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        });

        var confirmDialog = new ContentDialog
        {
            Title = "⚔ Push to Repo",
            Content = contentPanel,
            PrimaryButtonText = "Push",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = Content.XamlRoot,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface2Brush"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderHighBrush"],
            CornerRadius = new CornerRadius(8),
            RequestedTheme = ElementTheme.Dark
        };
        ApplyDialogTheme(confirmDialog);

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (charComboBox.SelectedItem is not ComboBoxItem selected)
            return;
        var characterPath = selected.Tag as string;
        if (string.IsNullOrEmpty(characterPath))
            return;

        _syncCts?.Cancel();
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

        ShowStatus("Loading commit history...", true);

        List<GitCommitEntry> commits;
        try
        {
            commits = await _addonSyncService.GetCommitLogAsync(game);
        }
        catch
        {
            ShowStatus("Failed to load commit history.", false);
            return;
        }

        if (commits.Count == 0)
        {
            ShowStatus("No commits found in the repo.", false);
            return;
        }

        // Build commit list
        var commitListView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 250,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4)
        };

        foreach (var commit in commits)
        {
            // Format the date for display
            var dateDisplay = "";
            if (DateTimeOffset.TryParse(commit.DateString, out var dto))
                dateDisplay = dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            else if (!string.IsNullOrEmpty(commit.DateString))
                dateDisplay = commit.DateString;

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            headerPanel.Children.Add(new TextBlock
            {
                Text = commit.Hash,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrEmpty(dateDisplay))
            {
                headerPanel.Children.Add(new TextBlock
                {
                    Text = dateDisplay,
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                    FontSize = 10,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextDimBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var item = new ListViewItem
            {
                Tag = commit,
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 2,
                    Children =
                    {
                        headerPanel,
                        new TextBlock
                        {
                            Text = commit.Message,
                            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                            FontSize = 12,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            commitListView.Items.Add(item);
        }
        commitListView.SelectedIndex = 0;

        // Addon detail panel — shown below the list when a commit is selected
        var addonDetailHeader = new TextBlock
        {
            Text = "📦 Included Addons",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
            FontSize = 10,
            CharacterSpacing = 100,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldDarkBrush"],
            Visibility = Visibility.Collapsed
        };

        var addonDetailText = new TextBlock
        {
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var addonDetailScroller = new ScrollViewer
        {
            MaxHeight = 120,
            Content = addonDetailText,
            Visibility = Visibility.Collapsed
        };

        void UpdateAddonDetail(GitCommitEntry commit)
        {
            var addons = ParseAddonListFromBody(commit.Body);
            if (addons.Count > 0)
            {
                addonDetailHeader.Visibility = Visibility.Visible;
                addonDetailScroller.Visibility = Visibility.Visible;
                addonDetailText.Visibility = Visibility.Visible;
                addonDetailText.Text = string.Join(",  ", addons);
            }
            else
            {
                addonDetailHeader.Visibility = Visibility.Collapsed;
                addonDetailScroller.Visibility = Visibility.Collapsed;
            }
        }

        // Show detail for the initially selected commit
        UpdateAddonDetail(commits[0]);

        commitListView.SelectionChanged += (_, _) =>
        {
            if (commitListView.SelectedItem is ListViewItem sel && sel.Tag is GitCommitEntry c)
                UpdateAddonDetail(c);
        };

        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = "Select the commit to restore. This will reset the repo to that snapshot and push.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });
        contentPanel.Children.Add(commitListView);
        contentPanel.Children.Add(addonDetailHeader);
        contentPanel.Children.Add(addonDetailScroller);
        contentPanel.Children.Add(new TextBlock
        {
            Text = "⚠ The repo will be overwritten. Run Pull afterwards to apply the restored state locally.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        });

        var confirmDialog = new ContentDialog
        {
            Title = "⚔ Rollback to Previous Sync",
            Content = contentPanel,
            PrimaryButtonText = "Rollback",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = Content.XamlRoot,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface2Brush"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderHighBrush"],
            CornerRadius = new CornerRadius(8),
            RequestedTheme = ElementTheme.Dark,
            MinWidth = 600,
            MaxWidth = 700
        };
        confirmDialog.Resources["ContentDialogMaxWidth"] = 700.0;
        confirmDialog.Resources["ContentDialogMinWidth"] = 600.0;
        ApplyDialogTheme(confirmDialog);

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (commitListView.SelectedItem is not ListViewItem selectedItem ||
            selectedItem.Tag is not GitCommitEntry selectedCommit)
            return;

        _syncCts?.Cancel();
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

    private static void ApplyDialogTheme(ContentDialog dialog)
    {
        // Primary button — emerald green gradient matching PrimaryButtonStyle
        var primaryStyle = new Style(typeof(Button));
        primaryStyle.Setters.Add(new Setter(Button.BackgroundProperty,
            new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = (Windows.UI.Color)Application.Current.Resources["EmeraldColor"], Offset = 0 },
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 45, 90, 70), Offset = 1 }
                }
            }));
        primaryStyle.Setters.Add(new Setter(Button.ForegroundProperty,
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 200, 240, 216))));
        primaryStyle.Setters.Add(new Setter(Button.BorderBrushProperty,
            new Microsoft.UI.Xaml.Media.SolidColorBrush { Color = (Windows.UI.Color)Application.Current.Resources["EmeraldLightColor"], Opacity = 0.3 }));
        primaryStyle.Setters.Add(new Setter(Button.CornerRadiusProperty, new CornerRadius(6)));
        primaryStyle.Setters.Add(new Setter(Button.FontWeightProperty, new Windows.UI.Text.FontWeight(700)));
        dialog.PrimaryButtonStyle = primaryStyle;

        // Default implicit button style — matches SecondaryButtonStyle for close/cancel buttons.
        // The PrimaryButtonStyle set above overrides this for the confirm button.
        var secondaryBase = (Style)Application.Current.Resources["SecondaryButtonStyle"];
        dialog.Resources[typeof(Button)] = new Style(typeof(Button)) { BasedOn = secondaryBase };

        // Dialog chrome
        dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["Surface2Brush"];
        dialog.Resources["ContentDialogTopOverlay"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Resources["ContentDialogBorderWidth"] = new Thickness(1);
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = Application.Current.Resources["BorderBrush"];

        // Title
        dialog.Resources["ContentDialogTitleForeground"] = Application.Current.Resources["TextPrimaryBrush"];

        // ComboBox dropdown
        dialog.Resources["ComboBoxDropDownBackground"] = Application.Current.Resources["Surface2Brush"];
        dialog.Resources["ComboBoxDropDownBorderBrush"] = Application.Current.Resources["BorderHighBrush"];
        dialog.Resources["ComboBoxItemForeground"] = Application.Current.Resources["TextPrimaryBrush"];
        dialog.Resources["ComboBoxItemForegroundSelected"] = Application.Current.Resources["TextPrimaryBrush"];
        dialog.Resources["ComboBoxItemForegroundPointerOver"] = Application.Current.Resources["GoldLightBrush"];
        dialog.Resources["ComboBoxItemBackgroundPointerOver"] = Application.Current.Resources["Surface3Brush"];
        dialog.Resources["ComboBoxItemBackgroundSelected"] = Application.Current.Resources["Surface3Brush"];
        dialog.Resources["ComboBoxItemBackgroundSelectedPointerOver"] = Application.Current.Resources["Surface3Brush"];
    }

    private static List<string> ParseAddonListFromBody(string body)
    {
        var addons = new List<string>();
        if (string.IsNullOrWhiteSpace(body))
            return addons;

        var inAddonSection = false;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals("Addons:", StringComparison.OrdinalIgnoreCase))
            {
                inAddonSection = true;
                continue;
            }

            if (inAddonSection)
            {
                if (line.StartsWith("- "))
                    addons.Add(line[2..].Trim());
                else if (line.Length == 0 && addons.Count > 0)
                    break;
            }
        }
        return addons;
    }
}
