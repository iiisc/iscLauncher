using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace iscLauncher;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private UpdateCheckResult? _startupUpdate;
    private GameEntry? _gameBeforeOptions;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppUpdateService.CurrentVersion}";

        var hwnd = WindowNative.GetWindowHandle(this);
        _services = AppServices.Create(hwnd);

        GameList.SetGames(_services.Library.Games);

        GameDetail.Initialize(_services);
        OptionsPanel.Initialize(_services);

        GameList.GameSelected     += OnGameSelected;
        GameList.LaunchRequested  += OnLaunchRequested;
        GameList.AddGameRequested += OnAddGameRequested;
        GameList.OptionsRequested += OnOptionsRequested;
        GameList.RemoveRequested  += async (_, game) => await GameDetail.ConfirmRemoveAsync(game);

        GameDetail.GameSaved      += OnGameSaved;
        GameDetail.EscapeRequested += OnDetailEscapeRequested;
        GameDetail.DeleteConfirmed += OnDeleteConfirmed;

        OptionsPanel.CloseRequested += OnOptionsCloseRequested;

        Closed += (_, _) => _services.Launcher.CancelPendingClipboardClear();

        SetWindowSize(hwnd, 900, 650);
        SetupCustomTitleBar();
        _ = LoadAsync();
    }

    /// <summary>Shows one panel, with <paramref name="game"/> selected in the list.</summary>
    private void ShowPanel(UIElement panel, GameEntry? game = null, bool adding = false)
    {
        GameList.SetSelectedGame(game);
        EmptyState.Visibility   = panel == EmptyState   ? Visibility.Visible : Visibility.Collapsed;
        GameDetail.Visibility   = panel == GameDetail   ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = panel == OptionsPanel ? Visibility.Visible : Visibility.Collapsed;
        GameList.SetAddGameHighlight(adding);
        GameList.SetOptionsHighlight(panel == OptionsPanel);
    }

    private void ShowDetail(GameEntry game, string? statusMessage = null)
    {
        ShowPanel(GameDetail, game);
        GameDetail.LoadGame(game, statusMessage);
    }

    private void ShowEmptyState() => ShowPanel(EmptyState);

    private void ShowAddGame()
    {
        ShowPanel(GameDetail, adding: true);
        GameDetail.StartNewGame();
    }

    private void ShowOptions()
    {
        // Left selected, the game's card couldn't be clicked to go back to it
        _gameBeforeOptions = GameList.SelectedGame;
        ShowPanel(OptionsPanel);
        OptionsPanel.Activate();
        if (_startupUpdate != null)
            OptionsPanel.ShowAvailableUpdate(_startupUpdate);
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
        // Removing another game from its card's menu leaves the one on screen, and any sync it's running, alone
        var shown = GameDetail.CurrentGame == game;
        if (shown) GameDetail.CancelSync();
        _services.Credentials.DeleteCredential(game.CredentialTarget);
        await _services.Library.RemoveAsync(game);
        // Leftovers only cost disk space, so a file that's still locked shouldn't fail the delete
        try { await Task.Run(() => _services.Sync.DeleteLocalData(game)); } catch { }
        if (!shown) return;
        if (_services.Library.Games.FirstOrDefault() is { } first) ShowDetail(first);
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
        // A game that was just added isn't selected in the list yet
        var added = GameList.SelectedGame != game;
        ShowDetail(game, added ? "Game added successfully!" : "Game updated successfully!");
    }

    private void OnOptionsCloseRequested(object? sender, EventArgs e)
    {
        if (_gameBeforeOptions is { } game && _services.Library.Games.Contains(game)) ShowDetail(game);
        else ShowEmptyState();
    }

    private void OnDetailEscapeRequested(object? sender, EventArgs e) => ShowEmptyState();

    private async Task LoadAsync()
    {
        try
        {
            await _services.Library.LoadAsync();
            if (_services.Library.Games.FirstOrDefault() is { } first) ShowDetail(first);
            _ = CheckForUpdatesSilentlyAsync();
            _ = CheckForRemoteChangesAsync();
            _ = CheckForAddonUpdatesAsync();
        }
        catch (Exception ex)
        {
            var d = DialogHelper.CreateThemedDialog(Content.XamlRoot, "Load Error");
            d.Content = $"Failed to load games: {ex.Message}";
            _ = d.ShowAsync();
        }
    }

    // Marks the games another PC has pushed to since this one last synced. Quiet: offline just shows nothing.
    private async Task CheckForRemoteChangesAsync()
    {
        // Sync uses the GitHub sign-in, so one GitHub has turned down is worth saying before it fails a pull
        if (_services.GitHub.Token != null)
        {
            try { await _services.GitHub.GetLoginAsync(); }
            catch (Exception ex) { Log.Write($"Couldn't check the GitHub sign-in: {ex.Message}"); }
        }
        // At once, so one unreachable repo doesn't hold up the rest
        await Task.WhenAll(_services.Library.Games.Where(g => g.HasSyncRepo).ToList().Select(async game =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await _services.Sync.RefreshRemoteHeadAsync(game, timeout.Token); }
            catch (Exception ex) { Log.Write($"Couldn't check {game.Name}'s repo: {ex.Message}"); }
        }));
    }

    // Marks the games whose addons have a newer version on GitHub. Quiet, like the check above.
    private async Task CheckForAddonUpdatesAsync()
    {
        // Signed out, GitHub allows 60 requests an hour, which checking every addon at each start soon uses up, leaving
        // none for the Addons tab. The tab checks for itself when it's opened.
        if (_services.GitHub.UsableToken == null)
            return;
        foreach (var game in _services.Library.Games.Where(g => File.Exists(g.ExecutablePath)).ToList())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await _services.AddonUpdates.RefreshAsync(game, timeout.Token); }
            catch (Exception ex) { Log.Write($"Couldn't check {game.Name}'s addons for updates: {ex.Message}"); }
        }
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            var result = await _services.Updates.CheckForUpdateAsync();
            if (result.UpdateAvailable && result.DownloadUrl != null)
            {
                _startupUpdate = result;
                GameList.ShowUpdateAvailable(result.LatestVersion);
            }
        }
        // Being offline or rate-limited at startup isn't worth interrupting for; Options can check again
        catch (Exception ex) { Log.Write($"Startup update check failed: {ex.Message}"); }
    }

    private void SetWindowSize(IntPtr hwnd, int width, int height)
    {
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        // Below this the game list and the game page no longer fit side by side
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetupCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _ = ShowTitleBarIconAsync();
    }

    // Taken from the exe, which has the icon built in, so no .ico file has to ship beside it
    private async Task ShowTitleBarIconAsync() =>
        TitleBarIcon.Source = await IconExtractor.GetIconFromExecutableAsync(Environment.ProcessPath!);
}
