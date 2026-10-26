using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace iscLauncher.Controls;

public sealed partial class GameListControl : UserControl
{
    public event EventHandler<GameEntry?>? GameSelected;
    public event EventHandler<GameEntry>? LaunchRequested;
    public event EventHandler? AddGameRequested;
    public event EventHandler? OptionsRequested;

    private bool _suppressSelectionChange;

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

    private void OnGameIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not GameEntry game) return;
        // x:Bind can't load the icon asynchronously, so reload it whenever the entry changes (e.g. a new executable)
        PropertyChangedEventHandler onChanged = (_, _) => _ = LoadIconAsync(image, game);
        game.PropertyChanged += onChanged;
        image.Unloaded += (_, _) => game.PropertyChanged -= onChanged;
        _ = LoadIconAsync(image, game);
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
