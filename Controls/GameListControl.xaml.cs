using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

    private bool _suppressSelectionChange;
    // The card the shared right-click menu was opened on
    private GameEntry? _menuGame;

    public GameListControl() => InitializeComponent();

    public void SetGames(ObservableCollection<GameEntry> games) =>
        GameListView.ItemsSource = games;

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

    private void OnGameMenuOpening(object? sender, object e) =>
        _menuGame = ((sender as MenuFlyout)?.Target as FrameworkElement)?.DataContext as GameEntry;

    private void OnRemoveMenuClick(object sender, RoutedEventArgs e)
    {
        if (_menuGame != null) RemoveRequested?.Invoke(this, _menuGame);
    }

    private void OnGameIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image && image.DataContext is GameEntry game)
            Watch(image, game, () => _ = LoadIconAsync(image, game));
    }

    private void OnClientLabelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock label && label.DataContext is GameEntry game)
            Watch(label, game, () => label.Text = ClientLabel(game));
    }

    // x:Bind can't derive these from the exe, so they're refreshed whenever the entry changes (e.g. a new executable)
    private static void Watch(FrameworkElement element, GameEntry game, Action refresh)
    {
        PropertyChangedEventHandler onChanged = (_, _) => refresh();
        game.PropertyChanged += onChanged;
        element.Unloaded += (_, _) => game.PropertyChanged -= onChanged;
        refresh();
    }

    /// <summary>The client's version, e.g. "WoW 3.3.5". An official install's name already says that, so its tag is "Battle.net".</summary>
    private static string ClientLabel(GameEntry game)
    {
        var exe = game.ExecutablePath;
        if (BattleNetInstall.IsOfficial(exe))
            return "Battle.net";
        try
        {
            // Private-server clients write the version as "3, 3, 5, 12340"
            var parts = Regex.Matches(FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "", @"\d+").Take(3).Select(m => m.Value).ToList();
            if (parts.Count == 3)
                return "WoW " + string.Join('.', parts);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { }
        return game.ExecutableDisplayPath;
    }

    private static async Task LoadIconAsync(Image image, GameEntry game)
    {
        var icon = await IconExtractor.GetIconFromExecutableAsync(game.ExecutablePath);
        image.Source = icon;
        if (image.Parent is Grid grid)
            foreach (var child in grid.Children)
                if (child is FontIcon fallback) { fallback.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed; break; }
    }

    private void OnAddGameClick(object sender, RoutedEventArgs e) =>
        AddGameRequested?.Invoke(this, EventArgs.Empty);

    private void OnOptionsClick(object sender, RoutedEventArgs e) =>
        OptionsRequested?.Invoke(this, EventArgs.Empty);
}
