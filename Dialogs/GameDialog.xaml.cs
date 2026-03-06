using System;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace iscLauncher.Dialogs;

public sealed partial class GameDialog : ContentDialog
{
    public GameEntry? GameEntry { get; private set; }
    public string? Password { get; private set; }

    private readonly GameEntry? _existingGame;
    private readonly Window _parentWindow;
    private readonly AppTypeDetector _appTypeDetector = new();

    public GameDialog(Window parentWindow, GameEntry? existingGame = null)
    {
        InitializeComponent();
        _parentWindow = parentWindow;
        _existingGame = existingGame;

        if (existingGame != null)
        {
            Title = "Edit Game";
            GameNameTextBox.Text = existingGame.Name;
            ExecutablePathTextBox.Text = existingGame.ExecutablePath;
            WindowTitleTextBox.Text = existingGame.WindowTitle ?? string.Empty;
            InputMethodComboBox.SelectedIndex = (int)existingGame.InputMethod;
            RealmlistAddressTextBox.Text = existingGame.RealmlistAddress ?? string.Empty;
            RealmNameTextBox.Text = existingGame.RealmName ?? string.Empty;
            AccountNameTextBox.Text = existingGame.AccountName ?? string.Empty;
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_parentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ExecutablePathTextBox.Text = file.Path;

            if (string.IsNullOrWhiteSpace(GameNameTextBox.Text))
            {
                GameNameTextBox.Text = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            }

            // Auto-detect the best input method
            var detection = _appTypeDetector.DetectAppType(file.Path);
            InputMethodComboBox.SelectedIndex = (int)detection.SuggestedMethod;

            // Show detection result in the info bar
            ErrorInfoBar.Severity = InfoBarSeverity.Informational;
            ErrorInfoBar.Message = $"Auto-detected: {detection.Reason}";
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(GameNameTextBox.Text))
        {
            ShowError("Please enter a game name.");
            args.Cancel = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(ExecutablePathTextBox.Text))
        {
            ShowError("Please select an executable file.");
            args.Cancel = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(GamePasswordBox.Password) && _existingGame == null)
        {
            ShowError("Please enter a password.");
            args.Cancel = true;
            return;
        }

        GameEntry = _existingGame ?? new GameEntry();
        GameEntry.Name = GameNameTextBox.Text.Trim();
        GameEntry.ExecutablePath = ExecutablePathTextBox.Text.Trim();
        GameEntry.WindowTitle = string.IsNullOrWhiteSpace(WindowTitleTextBox.Text) 
            ? null 
            : WindowTitleTextBox.Text.Trim();
        GameEntry.InputMethod = (PasswordInputMethod)InputMethodComboBox.SelectedIndex;
        GameEntry.RealmlistAddress = string.IsNullOrWhiteSpace(RealmlistAddressTextBox.Text)
            ? null
            : RealmlistAddressTextBox.Text.Trim();
        GameEntry.RealmName = string.IsNullOrWhiteSpace(RealmNameTextBox.Text)
            ? null
            : RealmNameTextBox.Text.Trim();
        GameEntry.AccountName = string.IsNullOrWhiteSpace(AccountNameTextBox.Text)
            ? null
            : AccountNameTextBox.Text.Trim();

        Password = string.IsNullOrWhiteSpace(GamePasswordBox.Password) 
            ? null 
            : GamePasswordBox.Password;
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}
