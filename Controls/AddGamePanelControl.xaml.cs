using System;
using System.IO;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace iscLauncher.Controls;

public sealed partial class AddGamePanelControl : UserControl
{
    public event EventHandler<GameEntry>? GameAdded;
    public event EventHandler? Cancelled;

    public CredentialService? CredentialService { get; set; }
    public GameRepository? GameRepository { get; set; }
    public IntPtr OwnerHwnd { get; set; }

    public AddGamePanelControl()
    {
        InitializeComponent();
    }

    public void ClearAndFocus()
    {
        NameTextBox.Text = string.Empty;
        ExecutableTextBox.Text = string.Empty;
        PasswordBox.Password = string.Empty;
        InputMethodComboBox.SelectedIndex = 0;
        WindowTitleTextBox.Text = string.Empty;
        RealmlistTextBox.Text = string.Empty;
        AccountTextBox.Text = string.Empty;
        RealmTextBox.Text = string.Empty;
        SyncRepoUrlTextBox.Text = string.Empty;
        SyncBranchTextBox.Text = string.Empty;
        FormInfoBar.IsOpen = false;
        NameTextBox.Focus(FocusState.Programmatic);
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        InitializeWithWindow.Initialize(picker, OwnerHwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        ExecutableTextBox.Text = file.Path;
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            NameTextBox.Text = Path.GetFileNameWithoutExtension(file.Name);

        var detection = new AppTypeDetector().DetectAppType(file.Path);
        InputMethodComboBox.SelectedIndex = (int)detection.SuggestedMethod;
        FormInfoBar.Severity = InfoBarSeverity.Informational;
        FormInfoBar.Message = $"Auto-detected: {detection.Reason}";
        FormInfoBar.IsOpen = true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            ShowError("Game name is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ExecutableTextBox.Text))
        {
            ShowError("Executable path is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            ShowError("Password is required.");
            return;
        }

        var game = new GameEntry
        {
            Name = NameTextBox.Text.Trim(),
            ExecutablePath = ExecutableTextBox.Text.Trim(),
            RealmlistAddress = NullIfBlank(RealmlistTextBox.Text),
            AccountName = NullIfBlank(AccountTextBox.Text),
            RealmName = NullIfBlank(RealmTextBox.Text),
            WindowTitle = NullIfBlank(WindowTitleTextBox.Text),
            InputMethod = InputMethodComboBox.SelectedIndex switch
            {
                1 => PasswordInputMethod.Clipboard,
                _ => PasswordInputMethod.SendKeys
            },
            SyncRepoUrl = NullIfBlank(SyncRepoUrlTextBox.Text),
            SyncBranch = NullIfBlank(SyncBranchTextBox.Text)
        };

        CredentialService!.SaveCredential(game.CredentialTarget, PasswordBox.Password);
        await GameRepository!.AddGameAsync(game);
        GameAdded?.Invoke(this, game);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) =>
        Cancelled?.Invoke(this, EventArgs.Empty);

    private void ShowError(string message)
    {
        FormInfoBar.Severity = InfoBarSeverity.Error;
        FormInfoBar.Message = message;
        FormInfoBar.IsOpen = true;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
