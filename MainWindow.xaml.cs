using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace iscLauncher;

public sealed partial class MainWindow : Window
{
    private readonly GameRepository _gameRepository = new();
    private readonly CredentialService _credentialService = new();
    private readonly GameLauncherService _gameLauncherService;
    private readonly ObservableCollection<GameEntry> _games = new();

    public MainWindow()
    {
        InitializeComponent();
        _gameLauncherService = new GameLauncherService(_credentialService);
        GameListView.ItemsSource = _games;

        // Set window size and custom title bar
        SetWindowSize(500, 600);
        SetupCustomTitleBar();

        _ = LoadGamesAsync();
    }

    private void SetWindowSize(int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void SetupCustomTitleBar()
    {
        // Extend content into title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private async Task LoadGamesAsync()
    {
        var library = await _gameRepository.LoadAsync();
        _games.Clear();
        foreach (var game in library.Games)
        {
            _games.Add(game);
        }
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is GameEntry game)
        {
            var oldPath = game.ExecutablePath;
            var dialog = new GameDialog(this, game);
            dialog.XamlRoot = Content.XamlRoot;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && dialog.GameEntry != null)
            {
                // Update password if provided
                if (!string.IsNullOrEmpty(dialog.Password))
                {
                    _credentialService.SaveCredential(dialog.GameEntry.CredentialTarget, dialog.Password);
                }

                // Invalidate icon cache if executable path changed
                if (oldPath != dialog.GameEntry.ExecutablePath)
                {
                    Services.IconExtractor.InvalidateCache(oldPath);
                }

                // Update game in repository
                await _gameRepository.UpdateGameAsync(dialog.GameEntry);

                // Refresh list
                await LoadGamesAsync();

                ShowStatus("Game updated successfully!", true);
            }
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
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Delete credential
                _credentialService.DeleteCredential(game.CredentialTarget);

                // Remove from repository
                await _gameRepository.RemoveGameAsync(game.Id);
                _games.Remove(game);
                UpdateEmptyState();

                ShowStatus("Game deleted.", true);
            }
        }
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
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
        // Show immediate feedback
        ShowStatus($"Launching {game.Name}...", true);

        var result = await _gameLauncherService.LaunchGameAsync(game);
        ShowStatus(result.Message, result.Success);

        // Auto-hide success messages after 5 seconds
        if (result.Success)
        {
            _ = HideStatusAfterDelay(5000);
        }
    }

    private string _lastStatusMessage = string.Empty;

    private void ShowStatus(string message, bool isSuccess)
    {
        _lastStatusMessage = message;
        StatusText.Text = message.Length > 50 ? message.Substring(0, 47) + "..." : message;
        StatusIcon.Glyph = isSuccess ? "\uE73E" : "\uE783"; // Checkmark or Warning
        StatusIcon.Foreground = isSuccess 
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        StatusPanel.Visibility = Visibility.Visible;
    }

    private async Task HideStatusAfterDelay(int delayMs)
    {
        await Task.Delay(delayMs);
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCopyStatusClick(object sender, RoutedEventArgs e)
    {
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(_lastStatusMessage);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

        // Brief feedback
        StatusText.Text = "Copied to clipboard!";
        _ = RestoreStatusTextAsync(1500);
    }

    private async Task RestoreStatusTextAsync(int delayMs)
    {
        await Task.Delay(delayMs);
        StatusText.Text = _lastStatusMessage.Length > 50 
            ? _lastStatusMessage.Substring(0, 47) + "..." 
            : _lastStatusMessage;
    }

    private void OnGameIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Image image && 
            image.DataContext is GameEntry game)
        {
            var icon = Services.IconExtractor.GetIconFromExecutable(game.ExecutablePath);
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
}
