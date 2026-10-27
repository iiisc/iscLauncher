using System;
using System.Collections.ObjectModel;
using System.IO;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace iscLauncher.Controls;

public sealed partial class GameListControl : UserControl
{
    public event EventHandler<GameEntry?>? GameSelected;
    public event EventHandler<GameEntry>? LaunchRequested;
    public event EventHandler? AddGameRequested;
    public event EventHandler? OptionsRequested;
    public event EventHandler<GameEntry>? RemoveRequested;
    public event EventHandler? Reordered;

    private bool _suppressSelectionChange;
    // The card the shared right-click menu was opened on
    private GameEntry? _menuGame;
    private ObservableCollection<GameEntry> _games = [];

    public GameListControl() => InitializeComponent();

    public void SetGames(ObservableCollection<GameEntry> games) =>
        GameListView.ItemsSource = _games = games;

    public GameEntry? SelectedGame => GameListView.SelectedItem as GameEntry;

    public void SetSelectedGame(GameEntry? game)
    {
        _suppressSelectionChange = true;
        GameListView.SelectedItem = game;
        _suppressSelectionChange = false;
    }

    public void SetAddGameHighlight(bool selected) =>
        AddGameButton.Foreground = ApplyHighlight(AddGameButton, selected);

    public void SetOptionsHighlight(bool selected) =>
        OptionsButtonIcon.Foreground = ApplyHighlight(OptionsButton, selected);

    public void ShowUpdateAvailable(string version)
    {
        UpdateDot.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(OptionsButton, $"Options: version {version} is ready to install");
        AutomationProperties.SetName(OptionsButton, $"Options, version {version} is ready to install");
    }

    /// <summary>Styles the button's frame and returns the foreground brush for its content.</summary>
    private static Brush ApplyHighlight(Button button, bool selected)
    {
        button.BorderBrush = DialogHelper.Res<Brush>(selected ? "GoldBrush" : "BorderBrush");
        button.BorderThickness = new Thickness(selected ? 2 : 1);
        button.Background = selected
            ? DialogHelper.Res<Brush>("Surface3Brush")
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return DialogHelper.Res<Brush>(selected ? "GoldBrush" : "TextMutedBrush");
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange) return;
        GameSelected?.Invoke(this, GameListView.SelectedItem as GameEntry);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GameListView.SelectedItem is GameEntry game)
            LaunchRequested?.Invoke(this, game);
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && GameListView.SelectedItem is GameEntry game)
        {
            RemoveRequested?.Invoke(this, game);
            e.Handled = true;
        }
    }

    // The menu sits on the list's container for each card, which doesn't carry the game as its DataContext
    private void OnGameMenuOpening(object? sender, object e)
    {
        _menuGame = (sender as MenuFlyout)?.Target is { } card ? GameListView.ItemFromContainer(card) as GameEntry : null;
        var index = _menuGame == null ? -1 : _games.IndexOf(_menuGame);
        MoveToTopItem.Visibility = MoveUpItem.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoveDownItem.Visibility = index >= 0 && index < _games.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMoveToTopClick(object sender, RoutedEventArgs e) => MoveMenuGame(_ => 0);
    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MoveMenuGame(index => index - 1);
    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MoveMenuGame(index => index + 1);

    private void MoveMenuGame(Func<int, int> newIndex)
    {
        if (_menuGame == null) return;
        // Moving a card takes it out of the list and puts it back, which would deselect it and close the game on screen
        var selected = SelectedGame;
        _suppressSelectionChange = true;
        var index = _games.IndexOf(_menuGame);
        _games.Move(index, newIndex(index));
        SetSelectedGame(selected);
        Reordered?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoveMenuClick(object sender, RoutedEventArgs e)
    {
        if (_menuGame != null) RemoveRequested?.Invoke(this, _menuGame);
    }

    /// <summary>
    /// The client's version, e.g. "WoW 3.3.5", or failing that "Folder\Wow.exe". An official install's name already says
    /// its version, so its tag is "Battle.net".
    /// </summary>
    public static string ClientLabel(string exe)
    {
        if (BattleNetInstall.IsOfficial(exe))
            return "Battle.net";
        if (AddonFiles.ClientVersion(exe) is { } version)
            return $"WoW {version}";
        var folder = Path.GetFileName(Path.GetDirectoryName(exe));
        return string.IsNullOrEmpty(folder) ? Path.GetFileName(exe) : $@"{folder}\{Path.GetFileName(exe)}";
    }

    private void OnAddGameClick(object sender, RoutedEventArgs e) =>
        AddGameRequested?.Invoke(this, EventArgs.Empty);

    private void OnOptionsClick(object sender, RoutedEventArgs e) =>
        OptionsRequested?.Invoke(this, EventArgs.Empty);
}
