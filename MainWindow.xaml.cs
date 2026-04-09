using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using iscLauncher.Controls;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
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

    public MainWindow()
    {
        InitializeComponent();
        _gameLauncherService = new GameLauncherService(_credentialService);
        VersionText.Text = $"v{AppUpdateService.CurrentVersion}";

        var hwnd = WindowNative.GetWindowHandle(this);

        // Inject services
        GameList.SetGames(_games);

        GameDetail.GameRepository = _gameRepository;
        GameDetail.CredentialService = _credentialService;
        GameDetail.GameLauncherService = _gameLauncherService;
        GameDetail.AddonSyncService = _addonSyncService;
        GameDetail.OwnerHwnd = hwnd;

        OptionsPanel.GameRepository = _gameRepository;
        OptionsPanel.CredentialService = _credentialService;
        OptionsPanel.AppUpdateService = _updateService;
        OptionsPanel.OwnerHwnd = hwnd;
        OptionsPanel.Games = _games;

        AddGamePanel.GameRepository = _gameRepository;
        AddGamePanel.CredentialService = _credentialService;
        AddGamePanel.OwnerHwnd = hwnd;

        // Wire events
        GameList.GameSelected     += OnGameSelected;
        GameList.LaunchRequested  += OnLaunchRequested;
        GameList.DeleteConfirmed  += OnDeleteConfirmed;
        GameList.AddGameRequested += OnAddGameRequested;
        GameList.OptionsRequested += OnOptionsRequested;

        GameDetail.GameSaved      += OnGameSaved;
        GameDetail.EscapeRequested += OnDetailEscapeRequested;

        OptionsPanel.CloseRequested               += OnOptionsCloseRequested;
        OptionsPanel.LibraryImported              += OnLibraryImported;
        OptionsPanel.IconCacheCleared             += OnIconCacheCleared;
        OptionsPanel.CheckUpdatesOnStartupChanged += (_, v) => _checkUpdatesOnStartup = v;

        AddGamePanel.GameAdded  += OnGameAdded;
        AddGamePanel.Cancelled  += OnAddGameCancelled;

        Closed += (_, _) => _gameLauncherService.CancelPendingClipboardClear();

        GameList.Loaded += (_, _) =>
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => GameList.UpdateSelectionVisuals());

        SetWindowSize(900, 650);
        SetupCustomTitleBar();
        _ = LoadAsync();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void ShowDetail(GameEntry game, string? statusMessage = null)
    {
        EmptyState.Visibility   = Visibility.Collapsed;
        GameDetail.Visibility   = Visibility.Visible;
        OptionsPanel.Visibility = Visibility.Collapsed;
        AddGamePanel.Visibility = Visibility.Collapsed;
        GameList.SetAddGameHighlight(false);
        GameList.SetOptionsHighlight(false);
        GameDetail.LoadGame(game, statusMessage);
    }

    private void ShowEmptyState()
    {
        EmptyState.Visibility   = Visibility.Visible;
        GameDetail.Visibility   = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        AddGamePanel.Visibility = Visibility.Collapsed;
        GameList.SetAddGameHighlight(false);
        GameList.SetOptionsHighlight(false);
    }

    private void ShowAddGame()
    {
        EmptyState.Visibility   = Visibility.Collapsed;
        GameDetail.Visibility   = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        AddGamePanel.Visibility = Visibility.Visible;
        GameList.SetSelectedGame(null);
        GameList.SetAddGameHighlight(true);
        GameList.SetOptionsHighlight(false);
        AddGamePanel.ClearAndFocus();
    }

    private void ShowOptions()
    {
        EmptyState.Visibility   = Visibility.Collapsed;
        GameDetail.Visibility   = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Visible;
        AddGamePanel.Visibility = Visibility.Collapsed;
        GameList.SetOptionsHighlight(true);
        GameList.SetAddGameHighlight(false);
        OptionsPanel.Activate(_checkUpdatesOnStartup);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private async void OnGameSelected(object? sender, GameEntry? game)
    {
        if (GameDetail.Visibility == Visibility.Visible && GameDetail.IsEditing)
        {
            if (!await GameDetail.ConfirmNavigateAwayAsync())
            {
                GameList.SetSelectedGame(GameDetail.CurrentGame);
                return;
            }
        }
        GameDetail.CancelSync();
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
        if (GameDetail.Visibility == Visibility.Visible &&
            GameDetail.IsEditing &&
            !await GameDetail.ConfirmNavigateAwayAsync()) return;
        GameDetail.CancelSync();
        ShowAddGame();
    }

    private async void OnOptionsRequested(object? sender, EventArgs e)
    {
        if (OptionsPanel.Visibility == Visibility.Visible)
        { OnOptionsCloseRequested(sender, e); return; }
        if (GameDetail.Visibility == Visibility.Visible &&
            GameDetail.IsEditing &&
            !await GameDetail.ConfirmNavigateAwayAsync()) return;
        GameDetail.CancelSync();
        ShowOptions();
    }

    private async void OnGameSaved(object? sender, GameEntry savedGame)
    {
        // Reload from disk so computed properties (ExecutableDisplayPath etc.) refresh
        var library = await _gameRepository.LoadAsync();
        _games.Clear();
        foreach (var g in library.Games) _games.Add(g);
        var refreshed = _games.FirstOrDefault(g => g.Id == savedGame.Id);
        if (refreshed != null) { GameList.SetSelectedGame(refreshed); ShowDetail(refreshed, "Game updated successfully!"); }
    }

    private void OnGameAdded(object? sender, GameEntry game)
    {
        _games.Add(game);
        GameList.SetSelectedGame(game);
        ShowDetail(game, "Game added successfully!");
    }

    private void OnAddGameCancelled(object? sender, EventArgs e)
    {
        if (GameList.SelectedGame != null) ShowDetail(GameList.SelectedGame);
        else ShowEmptyState();
    }

    private void OnOptionsCloseRequested(object? sender, EventArgs e)
    {
        GameList.SetOptionsHighlight(false);
        OptionsPanel.Visibility = Visibility.Collapsed;
        if (GameList.SelectedGame != null) ShowDetail(GameList.SelectedGame);
        else ShowEmptyState();
    }

    private void OnLibraryImported(object? sender, LibraryImportedEventArgs e)
    {
        _checkUpdatesOnStartup = e.CheckUpdatesOnStartup;
        _games.Clear();
        foreach (var g in e.Games) _games.Add(g);
        GameList.SetSelectedGame(null);
        ShowEmptyState();
    }

    private void OnIconCacheCleared(object? sender, EventArgs e)
    {
        // Force ListView to re-evaluate icons by bouncing the collection
        var snapshot = _games.ToList();
        _games.Clear();
        foreach (var g in snapshot) _games.Add(g);
    }

    private void OnDetailEscapeRequested(object? sender, EventArgs e)
    {
        GameList.SetSelectedGame(null);
        ShowEmptyState();
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            var library = await _gameRepository.LoadAsync();
            _games.Clear();
            foreach (var g in library.Games) _games.Add(g);
            GameDetail.SetComputerName(string.IsNullOrWhiteSpace(library.ComputerName)
                ? Environment.MachineName : library.ComputerName);
            _checkUpdatesOnStartup = library.CheckUpdatesOnStartup;
            if (_games.Count > 0) { GameList.SetSelectedGame(_games[0]); ShowDetail(_games[0]); }
            if (_checkUpdatesOnStartup) _ = CheckForUpdatesSilentlyAsync();
        }
        catch (Exception ex)
        {
            var d = Helpers.DialogHelper.CreateThemedDialog(Content.XamlRoot, "Load Error");
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
                // Show a non-modal nudge by briefly surfacing the Options panel is not feasible silently.
                // The user will see the update when they open Options.
            }
        }
        catch { }
    }

    // ── Window setup ──────────────────────────────────────────────────────────

    private void SetWindowSize(int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow != null)
        {
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi / 96.0;
            appWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetupCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        if (AppTitleBar != null) SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            TitleBarIcon.Source = new BitmapImage(new Uri(iconPath));
    }
}
