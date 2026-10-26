using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    public GitHubService? GitHub { get; set; }
    public IntPtr OwnerHwnd { get; set; }
    public IReadOnlyCollection<GameEntry>? Games { get; set; }

    private GameEntry? _game;
    // A game being added: not in the library until saved
    private bool _isNew;
    private CancellationTokenSource? _syncCts;
    private bool _syncBusy;
    private readonly StatusNotifier _status;

    public GameEntry? CurrentGame => _game;

    public GameDetailControl()
    {
        InitializeComponent();
        _status = new StatusNotifier(StatusBar, StatusText);

        foreach (var box in new[] { NameTextBox, ExecutableTextBox, RealmlistTextBox, AccountTextBox, RealmTextBox, SyncRepoUrlTextBox })
            box.TextChanged += (_, _) => UpdateSaveButtons();
        PasswordBox.PasswordChanged += (_, _) => UpdateSaveButtons();
        StartupDelayNumberBox.ValueChanged += (_, _) => UpdateSaveButtons();
    }

    public void LoadGame(GameEntry game, string? statusMessage = null)
    {
        _game = game;
        _isNew = false;
        FoundInstallsSection.Visibility = Visibility.Collapsed;
        ShowOfficial(game.ExecutablePath);

        NameTextBox.Text = game.Name;
        ExecutableTextBox.Text = game.ExecutablePath;
        RealmlistTextBox.Text = game.RealmlistAddress ?? string.Empty;
        AccountTextBox.Text = game.AccountName ?? string.Empty;
        RealmTextBox.Text = game.RealmName ?? string.Empty;
        StartupDelayNumberBox.Value = game.StartupDelaySeconds;
        PasswordBox.Password = string.Empty;

        SyncRepoUrlTextBox.Text = game.SyncRepoUrl ?? string.Empty;
        UpdateLastSyncedText(game);

        DetailPivot.SelectedIndex = 0;
        UpdateLaunchButtonState(game);
        UpdateSaveButtons();

        if (statusMessage != null)
            ShowStatus(statusMessage, true);
        else
            StatusBar.IsOpen = false;
    }

    public void StartNewGame()
    {
        LoadGame(new GameEntry());
        _isNew = true;
        UpdateSaveButtons();
        ShowFoundInstalls();
        NameTextBox.Focus(FocusState.Programmatic);
    }

    private void ShowFoundInstalls()
    {
        var inLibrary = (Games ?? []).Select(g => g.ExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FoundInstallsPanel.Children.Clear();
        foreach (var exe in BattleNetInstall.FindInstalledGames().Where(e => !inLibrary.Contains(e)))
        {
            var name = BattleNetInstall.VersionName(exe);
            var button = new Button { Content = name, Style = DialogHelper.Res<Style>("SecondaryButtonStyle") };
            ToolTipService.SetToolTip(button, exe);
            button.Click += (_, _) =>
            {
                ExecutableTextBox.Text = exe;
                NameTextBox.Text = name;
                ShowOfficial(exe);
            };
            FoundInstallsPanel.Children.Add(button);
        }
        FoundInstallsSection.Visibility = FoundInstallsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task<bool> ConfirmNavigateAwayAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Unsaved Changes");
        dialog.Content = "You have unsaved changes that will be lost. Discard them?";
        dialog.PrimaryButtonText = "Discard";
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private int StartupDelay => double.IsNaN(StartupDelayNumberBox.Value) ? 0 : (int)StartupDelayNumberBox.Value;

    private bool HasUnsavedChanges => _game is { } game && (
        NameTextBox.Text.Trim() != game.Name
        || ExecutableTextBox.Text.Trim() != game.ExecutablePath
        || RealmlistTextBox.Text.NullIfBlank() != game.RealmlistAddress
        || AccountTextBox.Text.NullIfBlank() != game.AccountName
        || RealmTextBox.Text.NullIfBlank() != game.RealmName
        || SyncRepoUrlTextBox.Text.NullIfBlank() != game.SyncRepoUrl
        || StartupDelay != game.StartupDelaySeconds
        || PasswordBox.Password.Length > 0);

    // Save and Cancel replace Launch while there's something to save. The addon actions work from the saved settings,
    // so they wait too.
    private void UpdateSaveButtons()
    {
        var editing = _isNew || HasUnsavedChanges;
        LaunchButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditModeButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

        SetUpSyncButton.Visibility = string.IsNullOrWhiteSpace(SyncRepoUrlTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        var hasRepo = _game?.HasSyncRepo == true;
        CopyAddonsSection.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        SyncButtonsPanel.Visibility = !editing && hasRepo ? Visibility.Visible : Visibility.Collapsed;
        NoRepoHint.Visibility = !editing && !hasRepo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (_isNew) EscapeRequested?.Invoke(this, EventArgs.Empty);
        else if (_game != null) LoadGame(_game);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_game == null) return false;

        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        { ShowStatus("Game name is required.", false); return false; }
        if (string.IsNullOrWhiteSpace(ExecutableTextBox.Text))
        { ShowStatus("Executable path is required.", false); return false; }

        var game = _game;
        var oldPath = game.ExecutablePath;

        game.Name = NameTextBox.Text.Trim();
        game.ExecutablePath = ExecutableTextBox.Text.Trim();
        game.RealmlistAddress = RealmlistTextBox.Text.NullIfBlank();
        game.AccountName = AccountTextBox.Text.NullIfBlank();
        game.RealmName = RealmTextBox.Text.NullIfBlank();
        game.StartupDelaySeconds = StartupDelay;
        game.SyncRepoUrl = SyncRepoUrlTextBox.Text.NullIfBlank();

        if (!string.IsNullOrEmpty(PasswordBox.Password))
            CredentialService!.SaveCredential(game.CredentialTarget, PasswordBox.Password);

        if (oldPath != game.ExecutablePath)
            IconExtractor.InvalidateCache(oldPath);

        if (_isNew)
            await GameRepository!.AddGameAsync(game);
        else
            await GameRepository!.UpdateGameAsync(game);
        game.NotifyChanged();

        GameSaved?.Invoke(this, game);
        return true;
    }

    private async void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var file = await FormHelpers.PickFileAsync(OwnerHwnd, ".exe");
        if (file == null) return;
        ExecutableTextBox.Text = file.Path;
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            NameTextBox.Text = Path.GetFileNameWithoutExtension(file.Name);
        ShowOfficial(file.Path);
    }

    // Battle.net handles login for official installs, so the login settings don't apply
    private void ShowOfficial(string exePath)
    {
        var official = BattleNetInstall.IsOfficial(exePath);
        OfficialNote.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        ConnectionSection.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
        AutomationTab.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // While there's something to save, Escape belongs to the Cancel button
        if (_isNew || HasUnsavedChanges) return;
        EscapeRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void ShowStatus(string message, bool isSuccess) => _status.Show(message, isSuccess);
}
