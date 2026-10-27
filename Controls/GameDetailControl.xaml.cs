using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace iscLauncher.Controls;

public sealed partial class GameDetailControl : UserControl
{
    public event EventHandler<GameEntry>? GameSaved;
    public event EventHandler? EscapeRequested;

    private AppServices _services = null!;

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
        StartupDelayNumberBox.ValueChanged += (_, _) => UpdateSaveButtons();
        OfferPushCheckBox.Checked += (_, _) => UpdateSaveButtons();
        OfferPushCheckBox.Unchecked += (_, _) => UpdateSaveButtons();
    }

    public void Initialize(AppServices services)
    {
        _services = services;
        services.GitHub.SignInProblemChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_game != null) UpdateSyncStatus(_game); });
    }

    public void LoadGame(GameEntry game, string? statusMessage = null)
    {
        var otherGame = _game?.Id != game.Id;
        if (_game != null) _game.PropertyChanged -= OnGameChanged;
        game.PropertyChanged += OnGameChanged;
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
        PasswordBox.PlaceholderText = _services.Credentials.GetCredential(game.CredentialTarget) != null
            ? "A password is saved. Type a new one to replace it."
            : "No password saved";

        SyncRepoUrlTextBox.Text = game.SyncRepoUrl ?? string.Empty;
        OfferPushCheckBox.IsChecked = game.OfferPushOnClose;
        UpdateSyncStatus(game);
        CloseInstalledAddons();

        // Syncing is what a synced game is opened for; reloading the same game (e.g. after saving) keeps the tab
        if (otherGame || DetailPivot.SelectedItem == null)
            DetailPivot.SelectedItem = game.HasSyncRepo ? SyncTab : GeneralTab;
        UpdateLaunchButtonState(game);
        UpdateSaveButtons();
        // A save of the game on the Addons tab lists it afresh, in case its executable moved
        if (AddonsTabShown)
            ShowInstalledAddons(game);

        if (statusMessage != null)
            ShowStatus(statusMessage, true);
        else
            _status.Hide();
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
        var inLibrary = _services.Library.Games.Select(g => g.ExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FoundInstallsPanel.Children.Clear();
        foreach (var exe in BattleNetInstall.FindInstalledGames().Where(e => !inLibrary.Contains(e)))
        {
            var name = BattleNetInstall.VersionName(exe);
            var button = DialogHelper.StyledButton(name);
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
        return await DialogHelper.AskAsync(XamlRoot, "Unsaved Changes",
            "You have unsaved changes that will be lost. Discard them?", "Discard") == ContentDialogResult.Primary;
    }

    private bool IsEditing => _isNew || HasUnsavedChanges;

    private int StartupDelay => double.IsNaN(StartupDelayNumberBox.Value) ? 0 : (int)StartupDelayNumberBox.Value;

    private bool HasUnsavedChanges => _game is { } game && (
        NameTextBox.Text.Trim() != game.Name
        || ExecutableTextBox.Text.Trim() != game.ExecutablePath
        || RealmlistTextBox.Text.NullIfBlank() != game.RealmlistAddress
        || AccountTextBox.Text.NullIfBlank() != game.AccountName
        || RealmTextBox.Text.NullIfBlank() != game.RealmName
        || SyncRepoUrlTextBox.Text.NullIfBlank() != game.SyncRepoUrl
        || StartupDelay != game.StartupDelaySeconds
        || OfferPushCheckBox.IsChecked != game.OfferPushOnClose
        || PasswordBox.Password.Length > 0);

    // Save and Cancel replace Launch while there's something to save. The addon actions work from the saved settings,
    // so they wait too.
    private void UpdateSaveButtons()
    {
        var editing = IsEditing;
        LaunchButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditModeButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        SaveButtonText.Text = _isNew ? "Add Game" : "Save Changes";

        SetUpSyncButton.Visibility = string.IsNullOrWhiteSpace(SyncRepoUrlTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        var hasRepo = _game?.HasSyncRepo == true;
        CopyAddonsSection.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        SyncButtonsPanel.Visibility = !editing && hasRepo ? Visibility.Visible : Visibility.Collapsed;
        OfferPushCheckBox.Visibility = hasRepo ? Visibility.Visible : Visibility.Collapsed;
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
        game.OfferPushOnClose = OfferPushCheckBox.IsChecked == true;

        if (!string.IsNullOrEmpty(PasswordBox.Password))
            _services.Credentials.SaveCredential(game.CredentialTarget, PasswordBox.Password);

        if (oldPath != game.ExecutablePath)
            IconExtractor.InvalidateCache(oldPath);

        if (_isNew)
            await _services.Library.AddAsync(game);
        else
            await _services.Library.SaveAsync();
        game.NotifyChanged();

        GameSaved?.Invoke(this, game);
        return true;
    }

    private async void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var file = await FormHelpers.PickFileAsync(_services.OwnerHwnd, ".exe");
        if (file == null) return;
        ExecutableTextBox.Text = file.Path;
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            NameTextBox.Text = Path.GetFileNameWithoutExtension(file.Name);
        ShowOfficial(file.Path);
    }

    // Official clients find their own servers and remember the account, so only the password applies
    private void ShowOfficial(string exePath)
    {
        var official = BattleNetInstall.IsOfficial(exePath);
        OfficialNote.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        ConnectionSection.Visibility = official ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateSaveButtons();
        // Emptying the box, which opening a game does too, hides it again so a password is never left showing
        if (PasswordBox.Password.Length == 0)
            SetPasswordRevealed(false);
        RevealPasswordButton.Visibility = PasswordBox.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRevealPasswordClick(object sender, RoutedEventArgs e) =>
        SetPasswordRevealed(PasswordBox.PasswordRevealMode != PasswordRevealMode.Visible);

    private void SetPasswordRevealed(bool revealed)
    {
        PasswordBox.PasswordRevealMode = revealed ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        RevealPasswordIcon.Glyph = revealed ? "\uED1A" : "\uE7B3";
        var label = revealed ? "Hide password" : "Show password";
        ToolTipService.SetToolTip(RevealPasswordButton, label);
        AutomationProperties.SetName(RevealPasswordButton, label);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // While there's something to save, Escape belongs to the Cancel button
        if (IsEditing) return;
        EscapeRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void ShowStatus(string message, bool isSuccess) => _status.Show(message, isSuccess);

    private void ShowInfo(string message) => _status.ShowInfo(message);
}
