using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace iscLauncher.Controls;

public sealed record LibraryImportedEventArgs(IReadOnlyList<GameEntry> Games, bool CheckUpdatesOnStartup);

public sealed partial class OptionsPanelControl : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler<LibraryImportedEventArgs>? LibraryImported;
    public event EventHandler? IconCacheCleared;
    public event EventHandler<bool>? CheckUpdatesOnStartupChanged;

    public GameRepository? GameRepository { get; set; }
    public CredentialService? CredentialService { get; set; }
    public AppUpdateService? AppUpdateService { get; set; }
    public IntPtr OwnerHwnd { get; set; }
    public IReadOnlyCollection<GameEntry>? Games { get; set; }

    private UpdateCheckResult? _pendingUpdate;
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _statusHideCts;
    private bool _suppressSave;

    public OptionsPanelControl() => InitializeComponent();

    public void Activate(bool checkUpdatesOnStartup)
    {
        VersionText.Text = $"v{Services.AppUpdateService.CurrentVersion}";
        StatusBar.IsOpen = false;
        UpdateAvailableText.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        CheckUpdatesIcon.Glyph = "\uE72C";
        CheckUpdatesText.Text = "Check for Updates";
        _pendingUpdate = null;
        _updateCts?.Cancel();

        _suppressSave = true;
        CheckUpdatesOnStartupToggle.IsOn = checkUpdatesOnStartup;
        _suppressSave = false;
    }

    // ── Escape ────────────────────────────────────────────────────────────────

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    // ── Updates ───────────────────────────────────────────────────────────────

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateAvailableText.Visibility = Visibility.Collapsed;
            ShowStatus("Checking for updates...", true);
            try
            {
                _updateCts?.Cancel(); _updateCts?.Dispose();
                _updateCts = new CancellationTokenSource();
                var result = await AppUpdateService!.CheckForUpdateAsync(_updateCts.Token);
                if (!result.UpdateAvailable)
                {
                    ShowStatus($"You're up to date (v{Services.AppUpdateService.CurrentVersion}).", true);
                }
                else if (result.DownloadUrl == null)
                {
                    ShowStatus($"Version {result.LatestVersion} is available but has no downloadable asset.", false);
                }
                else
                {
                    StatusBar.IsOpen = false;
                    _pendingUpdate = result;
                    UpdateAvailableText.Text = $"Version {result.LatestVersion} is available. The app will restart after installing.";
                    UpdateAvailableText.Visibility = Visibility.Visible;
                    CheckUpdatesIcon.Glyph = "\uE896";
                    CheckUpdatesText.Text = "Download & Install";
                }
            }
            catch (OperationCanceledException) { StatusBar.IsOpen = false; }
            catch (Exception ex) { ShowStatus($"Update check failed: {ex.Message}", false); }
            finally { CheckUpdatesButton.IsEnabled = true; }
        }
        else
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateProgressBar.Value = 0;
            UpdateProgressBar.Visibility = Visibility.Visible;
            ShowStatus("Downloading update...", true);
            try
            {
                _updateCts?.Cancel(); _updateCts?.Dispose();
                _updateCts = new CancellationTokenSource();
                var progress = new Progress<double>(p =>
                {
                    UpdateProgressBar.Value = p;
                    StatusText.Text = $"Downloading update... {(int)(p * 100)}%";
                });
                await AppUpdateService!.DownloadAndApplyAsync(
                    _pendingUpdate.DownloadUrl!, _pendingUpdate.AssetName!, progress, _updateCts.Token);
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                ShowStatus($"Update failed: {ex.Message}", false);
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                CheckUpdatesButton.IsEnabled = true;
            }
        }
    }

    private async void OnCheckUpdatesOnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSave) return;
        var newValue = CheckUpdatesOnStartupToggle.IsOn;
        await GameRepository!.SetCheckUpdatesOnStartupAsync(newValue);
        CheckUpdatesOnStartupChanged?.Invoke(this, newValue);
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Services.GameRepository.AppDataFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Services.GameRepository.AppDataFolder) { UseShellExecute = true });
        }
        catch { ShowStatus("Could not open data folder.", false); }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "games_backup";
        picker.FileTypeChoices.Add("JSON file", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, OwnerHwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            var library = await GameRepository!.LoadAsync();
            var json = JsonSerializer.Serialize(library, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file.Path, json);
            ShowStatus($"Exported {library.Games.Count} game(s) successfully.", true);
        }
        catch (Exception ex) { ShowStatus($"Export failed: {ex.Message}", false); }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        InitializeWithWindow.Initialize(picker, OwnerHwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        GameLibrary? imported;
        try
        {
            var json = await File.ReadAllTextAsync(file.Path);
            imported = JsonSerializer.Deserialize<GameLibrary>(json);
        }
        catch { ShowStatus("Selected file is not a valid library backup.", false); return; }

        if (imported == null) { ShowStatus("Selected file is not a valid library backup.", false); return; }

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Import Library");
        dialog.Content = $"This will replace your current library with {imported.Games.Count} game(s) from the backup. Passwords are not included in exports and must be re-entered. Continue?";
        dialog.PrimaryButtonText = "Import";
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var destPath = Path.Combine(Services.GameRepository.AppDataFolder, "games.json");
            Directory.CreateDirectory(Services.GameRepository.AppDataFolder);
            File.Copy(file.Path, destPath, overwrite: true);
            var reloaded = await GameRepository.LoadAsync();

            _suppressSave = true;
            CheckUpdatesOnStartupToggle.IsOn = reloaded.CheckUpdatesOnStartup;
            _suppressSave = false;

            LibraryImported?.Invoke(this, new LibraryImportedEventArgs(
                reloaded.Games, reloaded.CheckUpdatesOnStartup));
            ShowStatus($"Imported {reloaded.Games.Count} game(s) successfully.", true);
        }
        catch (Exception ex) { ShowStatus($"Import failed: {ex.Message}", false); }
    }

    private void OnClearIconCacheClick(object sender, RoutedEventArgs e)
    {
        IconExtractor.ClearCache();
        IconCacheCleared?.Invoke(this, EventArgs.Empty);
        ShowStatus("Icon cache cleared.", true);
    }

    // ── Security ──────────────────────────────────────────────────────────────

    private async void OnRemoveAllPasswordsClick(object sender, RoutedEventArgs e)
    {
        if (Games == null || Games.Count == 0)
        { ShowStatus("No stored passwords to remove.", false); return; }

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Remove All Passwords");
        dialog.Content = $"This will permanently delete passwords for all {Games.Count} game(s) from Windows Credential Manager. This cannot be undone.";
        dialog.PrimaryButtonText = "Remove All";
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        int removed = 0;
        foreach (var game in Games)
            if (CredentialService!.DeleteCredential(game.CredentialTarget)) removed++;
        ShowStatus($"Removed {removed} password(s).", true);
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void ShowStatus(string message, bool isSuccess)
    {
        _statusHideCts?.Cancel(); _statusHideCts?.Dispose(); _statusHideCts = null;
        StatusText.Text = message;
        StatusBar.Severity = isSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
        if (isSuccess)
        {
            var cts = new CancellationTokenSource(); _statusHideCts = cts;
            _ = Task.Delay(5000, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) DispatcherQueue.TryEnqueue(() => StatusBar.IsOpen = false);
            });
        }
    }
}
