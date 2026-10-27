using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppLifecycle;

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
        _status.Hide();
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
        else if (_services.GitHub.SignInProblem is { } problem)
        {
            GitHubAccountText.Text = $"{problem}. Sign in again.";
            GitHubButtonText.Text = "Sign In";
        }
        ChooseReposLink.Visibility = _services.GitHub.Token != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        if (_services.GitHub.UsableToken != null)
        {
            _services.GitHub.SignOut();
            ShowStatus("Signed out of GitHub.", true);
        }
        else
        {
            GitHubButton.IsEnabled = false;
            // The sign-in it replaces could reach every repo, and signing in here doesn't take that access back on GitHub
            var outdated = _services.GitHub.SignInProblem == GitHubService.Outdated;
            try
            {
                if (await GitHubSignInDialog.ShowAsync(XamlRoot, _services.GitHub) is { } login)
                    ShowStatus(outdated
                        ? $"Signed in to GitHub as {login}. Now revoke the old access on GitHub: Settings → Applications → Authorized OAuth Apps → iscLauncher → Revoke."
                        : $"Signed in to GitHub as {login}.", true);
            }
            catch (Exception ex) { ShowStatus($"GitHub sign-in failed: {ex.Message}", false); }
            finally { GitHubButton.IsEnabled = true; }
        }
        await ShowGitHubAccountAsync();
    }

    private void OnChooseReposClick(object sender, RoutedEventArgs e)
    {
        if (!Shell.Open(GitHubService.InstallUrl))
            ShowStatus("Could not open browser.", false);
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
            catch (OperationCanceledException) { _status.Hide(); }
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
                var exe = await _services.Updates.DownloadAndApplyAsync(_pendingUpdate, progress, _updateCts.Token);
                // Still registered as the running copy, this one would be handed the new version's start and it would close
                AppInstance.GetCurrent().UnregisterKey();
                Process.Start(exe);
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
        _status.Hide();
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
        try { Directory.CreateDirectory(Services.GameRepository.AppDataFolder); }
        catch (IOException) { }
        if (!Shell.Open(Services.GameRepository.AppDataFolder))
            ShowStatus("Could not open data folder.", false);
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (!Shell.Open(Log.FilePath))
            ShowStatus("Could not open the log.", false);
    }

    private void OnReportProblemClick(object sender, RoutedEventArgs e)
    {
        // GitHub's issue forms fill in the fields named in the link; these are the ids in bug_report.yml
        var url = $"{AppUpdateService.RepoUrl}/issues/new?template=bug_report.yml" +
                  $"&version={Uri.EscapeDataString("v" + AppUpdateService.CurrentVersion)}" +
                  $"&os={Uri.EscapeDataString(RuntimeInformation.OSDescription)}";
        if (!Shell.Open(url))
            ShowStatus("Could not open browser.", false);
    }

    private void OnAddToStartMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "iscLauncher.lnk");
            Shortcut.Create(shortcutPath, Environment.ProcessPath!, AppContext.BaseDirectory);
            ShowStatus("iscLauncher is in the Start menu.", true);
        }
        catch (Exception ex) { ShowStatus($"Couldn't add the Start menu shortcut: {ex.Message}", false); }
    }

    private void ShowStatus(string message, bool isSuccess) => _status.Show(message, isSuccess);

    private void ShowInfo(string message) => _status.ShowInfo(message);
}
