using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using iscLauncher.Controls;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace iscLauncher;

public sealed partial class MainWindow : Window
{
    private readonly GameRepository _gameRepository = new();
    private readonly CredentialService _credentialService = new();
    private readonly GameLauncherService _gameLauncherService;
    private readonly AddonSyncService _addonSyncService = new(new GitService());
    private readonly AppUpdateService _updateService = new();
    private readonly ObservableCollection<GameEntry> _games = new();
    private bool _checkUpdatesOnStartup;
    private UpdateCheckResult? _startupUpdate;

    public MainWindow()
    {
        InitializeComponent();
        _gameLauncherService = new GameLauncherService(_credentialService);
        VersionText.Text = $"v{AppUpdateService.CurrentVersion}";

        var hwnd = WindowNative.GetWindowHandle(this);

        GameList.SetGames(_games);

        GameDetail.GameRepository = _gameRepository;
        GameDetail.CredentialService = _credentialService;
        GameDetail.GameLauncherService = _gameLauncherService;
        GameDetail.AddonSyncService = _addonSyncService;
        GameDetail.OwnerHwnd = hwnd;
        GameDetail.Games = _games;

        OptionsPanel.GameRepository = _gameRepository;
        OptionsPanel.CredentialService = _credentialService;
        OptionsPanel.AppUpdateService = _updateService;
        OptionsPanel.Games = _games;

        GameList.GameSelected     += OnGameSelected;
        GameList.LaunchRequested  += OnLaunchRequested;
        GameList.DeleteConfirmed  += OnDeleteConfirmed;
        GameList.AddGameRequested += OnAddGameRequested;
        GameList.OptionsRequested += OnOptionsRequested;

        GameDetail.GameSaved      += OnGameSaved;
        GameDetail.EscapeRequested += OnDetailEscapeRequested;

        OptionsPanel.CloseRequested               += OnOptionsCloseRequested;
        OptionsPanel.CheckUpdatesOnStartupChanged += (_, v) => _checkUpdatesOnStartup = v;

        Closed += (_, _) => _gameLauncherService.CancelPendingClipboardClear();

        GameList.Loaded += (_, _) =>
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, GameList.UpdateSelectionVisuals);

        SetWindowSize(hwnd, 900, 650);
        SetupCustomTitleBar();
        _ = LoadAsync();
    }

    private void ShowPanel(UIElement panel)
    {
        EmptyState.Visibility   = panel == EmptyState   ? Visibility.Visible : Visibility.Collapsed;
        GameDetail.Visibility   = panel == GameDetail   ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = panel == OptionsPanel ? Visibility.Visible : Visibility.Collapsed;
        GameList.SetAddGameHighlight(false);
        GameList.SetOptionsHighlight(panel == OptionsPanel);
    }

    private void ShowDetail(GameEntry game, string? statusMessage = null)
    {
        ShowPanel(GameDetail);
        GameDetail.LoadGame(game, statusMessage);
    }

    private void ShowEmptyState() => ShowPanel(EmptyState);

    private void ShowAddGame()
    {
        GameList.SetSelectedGame(null);
        ShowPanel(GameDetail);
        GameList.SetAddGameHighlight(true);
        GameDetail.StartNewGame();
    }

    private void ShowOptions()
    {
        ShowPanel(OptionsPanel);
        OptionsPanel.Activate(_checkUpdatesOnStartup);
    }

    /// <summary>Asks to discard unsaved edits, then stops any sync tied to the current game.</summary>
    private async Task<bool> TryLeaveDetailAsync()
    {
        if (GameDetail.Visibility == Visibility.Visible && !await GameDetail.ConfirmNavigateAwayAsync())
            return false;
        GameDetail.CancelSync();
        return true;
    }

    private async void OnGameSelected(object? sender, GameEntry? game)
    {
        if (!await TryLeaveDetailAsync())
        {
            GameList.SetSelectedGame(GameDetail.CurrentGame);
            return;
        }
        if (game == null) ShowEmptyState();
        else ShowDetail(game);
    }

    private async void OnLaunchRequested(object? sender, GameEntry game) =>
        await GameDetail.LaunchAsync(game);

    private async void OnDeleteConfirmed(object? sender, GameEntry game)
    {
        _credentialService.DeleteCredential(game.CredentialTarget);
        await _gameRepository.RemoveGameAsync(game.Id);
        _games.Remove(game);
        GameList.SetSelectedGame(null);
        if (_games.Count > 0) { GameList.SetSelectedGame(_games[0]); ShowDetail(_games[0]); }
        else ShowEmptyState();
    }

    private async void OnAddGameRequested(object? sender, EventArgs e)
    {
        if (await TryLeaveDetailAsync()) ShowAddGame();
    }

    private async void OnOptionsRequested(object? sender, EventArgs e)
    {
        if (OptionsPanel.Visibility == Visibility.Visible)
        { OnOptionsCloseRequested(sender, e); return; }
        if (await TryLeaveDetailAsync()) ShowOptions();
    }

    private void OnGameSaved(object? sender, GameEntry game)
    {
        var added = !_games.Contains(game);
        if (added)
        {
            _games.Add(game);
            GameList.SetSelectedGame(game);
        }
        ShowDetail(game, added ? "Game added successfully!" : "Game updated successfully!");
    }

    private void OnOptionsCloseRequested(object? sender, EventArgs e)
    {
        if (GameList.SelectedGame != null) ShowDetail(GameList.SelectedGame);
        else ShowEmptyState();
    }

    private void OnDetailEscapeRequested(object? sender, EventArgs e)
    {
        GameList.SetSelectedGame(null);
        ShowEmptyState();
    }

    private async Task LoadAsync()
    {
        try
        {
            var library = await _gameRepository.LoadAsync();
            foreach (var game in library.Games) _games.Add(game);
            GameDetail.SetComputerName(library.EffectiveComputerName);
            _checkUpdatesOnStartup = library.CheckUpdatesOnStartup;
            if (_games.Count > 0) { GameList.SetSelectedGame(_games[0]); ShowDetail(_games[0]); }
            if (_checkUpdatesOnStartup) _ = CheckForUpdatesSilentlyAsync();
        }
        catch (Exception ex)
        {
            var d = DialogHelper.CreateThemedDialog(Content.XamlRoot, "Load Error");
            d.Content = $"Failed to load games: {ex.Message}";
            _ = d.ShowAsync();
        }
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdateAsync();
            if (result.UpdateAvailable && result.DownloadUrl != null)
            {
                _startupUpdate = result;
                UpdateBar.Message = $"Version {result.LatestVersion} is ready to install.";
                UpdateBar.IsOpen = true;
            }
        }
        // Being offline or rate-limited at startup isn't worth interrupting for; Options can check again
        catch { }
    }

    private async void OnUpdateBarClick(object sender, RoutedEventArgs e)
    {
        if (_startupUpdate == null || !await TryLeaveDetailAsync()) return;
        UpdateBar.IsOpen = false;
        ShowOptions();
        OptionsPanel.ShowAvailableUpdate(_startupUpdate);
    }

    private void SetWindowSize(IntPtr hwnd, int width, int height)
    {
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetupCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            TitleBarIcon.Source = new BitmapImage(new Uri(iconPath));
    }
}
