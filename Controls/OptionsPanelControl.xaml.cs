using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace iscLauncher.Controls;

public sealed partial class OptionsPanelControl : UserControl
{
    public event EventHandler? CloseRequested;

    private AppServices _services = null!;

    private UpdateCheckResult? _pendingUpdate;
    private CancellationTokenSource? _updateCts;
    private readonly StatusNotifier _status;

    public OptionsPanelControl()
    {
        InitializeComponent();
        _status = new StatusNotifier(StatusBar, StatusText);
    }

    public void Initialize(AppServices services) => _services = services;

    public void Activate()
    {
        VersionText.Text = $"v{Services.AppUpdateService.CurrentVersion}";
        StatusBar.IsOpen = false;
        UpdateAvailableText.Visibility = Visibility.Collapsed;
        ReleaseNotesPanel.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        CheckUpdatesIcon.Glyph = "\uE72C";
        CheckUpdatesText.Text = "Check for Updates";
        _pendingUpdate = null;
        _updateCts?.Cancel();
        _ = ShowGitHubAccountAsync();
    }

    private async Task ShowGitHubAccountAsync()
    {
        var signedIn = _services.GitHub.Token != null;
        GitHubAccountText.Text = signedIn ? "Signed in to GitHub" : "Not signed in";
        GitHubButtonText.Text = signedIn ? "Sign Out" : "Sign In";
        if (signedIn && await _services.GitHub.GetLoginAsync() is { } login)
            GitHubAccountText.Text = $"Signed in to GitHub as {login}";
    }

    private async void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        if (_services.GitHub.Token != null)
        {
            _services.GitHub.SignOut();
            ShowStatus("Signed out of GitHub.", true);
        }
        else
        {
            GitHubButton.IsEnabled = false;
            try
            {
                if (await GitHubSignInDialog.ShowAsync(XamlRoot, _services.GitHub) is { } login)
                    ShowStatus($"Signed in to GitHub as {login}.", true);
            }
            catch (Exception ex) { ShowStatus($"GitHub sign-in failed: {ex.Message}", false); }
            finally { GitHubButton.IsEnabled = true; }
        }
        await ShowGitHubAccountAsync();
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateAvailableText.Visibility = Visibility.Collapsed;
            ShowInfo("Checking for updates...");
            try
            {
                _updateCts?.Cancel(); _updateCts?.Dispose();
                _updateCts = new CancellationTokenSource();
                var result = await _services.Updates.CheckForUpdateAsync(_updateCts.Token);
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
                    ShowAvailableUpdate(result);
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
            ShowInfo("Downloading update...");
            try
            {
                _updateCts?.Cancel(); _updateCts?.Dispose();
                _updateCts = new CancellationTokenSource();
                var progress = new Progress<double>(p =>
                {
                    UpdateProgressBar.Value = p;
                    StatusText.Text = $"Downloading update... {(int)(p * 100)}%";
                });
                await _services.Updates.DownloadAndApplyAsync(_pendingUpdate, progress, _updateCts.Token);
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

    public void ShowAvailableUpdate(UpdateCheckResult update)
    {
        StatusBar.IsOpen = false;
        _pendingUpdate = update;
        UpdateAvailableText.Text = $"Version {update.LatestVersion} is available. The app will restart after installing.";
        UpdateAvailableText.Visibility = Visibility.Visible;
        ReleaseNotesText.Text = update.Notes;
        ReleaseNotesPanel.Visibility = update.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesIcon.Glyph = "\uE896";
        CheckUpdatesText.Text = "Download & Install";
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Services.GameRepository.AppDataFolder);
            Process.Start(new ProcessStartInfo(
                Services.GameRepository.AppDataFolder) { UseShellExecute = true });
        }
        catch { ShowStatus("Could not open data folder.", false); }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (!Log.Open())
            ShowStatus("Could not open the log.", false);
    }

    private void OnAddToStartMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "iscLauncher.lnk");
            // .NET has no API for .lnk files; Windows' scripting shell is the simplest way to write one
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = Environment.ProcessPath;
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.Save();
            ShowStatus("iscLauncher is in the Start menu.", true);
        }
        catch (Exception ex) { ShowStatus($"Couldn't add the Start menu shortcut: {ex.Message}", false); }
    }

    private void ShowStatus(string message, bool isSuccess) => _status.Show(message, isSuccess);

    private void ShowInfo(string message) => _status.ShowInfo(message);
}
