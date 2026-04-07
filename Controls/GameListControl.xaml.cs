using System;
using System.Collections.ObjectModel;
using System.Numerics;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace iscLauncher.Controls;

public sealed partial class GameListControl : UserControl
{
    public event EventHandler<GameEntry?>? GameSelected;
    public event EventHandler<GameEntry>? LaunchRequested;
    public event EventHandler<GameEntry>? DeleteConfirmed;
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
        UpdateSelectionVisuals();
    }

    public void UpdateSelectionVisuals()
    {
        foreach (var item in GameListView.Items)
        {
            var container = GameListView.ContainerFromItem(item) as ListViewItem;
            if (container == null) continue;
            var border = FindVisualChild<Border>(container, "GameCardBorder");
            if (border == null) continue;
            bool isSelected = item == GameListView.SelectedItem;
            if (isSelected)
            {
                border.BorderBrush = (Brush)Application.Current.Resources["GoldBrush"];
                border.BorderThickness = new Thickness(2);
                border.Background = (Brush)Application.Current.Resources["Surface3Brush"];
                border.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                border.Translation = new Vector3(0, 0, 8);
            }
            else
            {
                border.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];
                border.BorderThickness = new Thickness(1);
                border.Background = (Brush)Application.Current.Resources["Surface2Brush"];
                border.Shadow = null;
                border.Translation = new Vector3(0, 0, 0);
            }
        }
    }

    public void SetAddGameHighlight(bool selected)
    {
        AddGameButton.BorderBrush = (Brush)Application.Current.Resources[selected ? "GoldBrush" : "BorderBrush"];
        AddGameButton.BorderThickness = new Thickness(selected ? 2 : 1);
        AddGameButton.Background = selected
            ? (Brush)Application.Current.Resources["Surface3Brush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AddGameButton.Foreground = (Brush)Application.Current.Resources[selected ? "GoldBrush" : "TextMutedBrush"];
    }

    public void SetOptionsHighlight(bool selected)
    {
        OptionsButton.BorderBrush = (Brush)Application.Current.Resources[selected ? "GoldBrush" : "BorderBrush"];
        OptionsButton.BorderThickness = new Thickness(selected ? 2 : 1);
        OptionsButton.Background = (Brush)Application.Current.Resources[selected ? "Surface3Brush" : "Surface2Brush"];
        OptionsButtonIcon.Foreground = (Brush)Application.Current.Resources[selected ? "GoldBrush" : "TextMutedBrush"];
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange) return;
        UpdateSelectionVisuals();
        GameSelected?.Invoke(this, GameListView.SelectedItem as GameEntry);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GameListView.SelectedItem is GameEntry game)
            LaunchRequested?.Invoke(this, game);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameEntry game }) return;
        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Delete Game");
        dialog.Content = $"Are you sure you want to delete '{game.Name}'? This will also remove the stored password.";
        dialog.PrimaryButtonText = "Delete";
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            DeleteConfirmed?.Invoke(this, game);
    }

    private async void OnGameIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not GameEntry game) return;
        var icon = await IconExtractor.GetIconFromExecutableAsync(game.ExecutablePath);
        if (icon == null) return;
        image.Source = icon;
        if (image.Parent is Grid grid)
            foreach (var child in grid.Children)
                if (child is FontIcon fallback) { fallback.Visibility = Visibility.Collapsed; break; }
    }

    private void OnAddGameClick(object sender, RoutedEventArgs e) =>
        AddGameRequested?.Invoke(this, EventArgs.Empty);

    private void OnOptionsClick(object sender, RoutedEventArgs e) =>
        OptionsRequested?.Invoke(this, EventArgs.Empty);

    private T? FindVisualChild<T>(DependencyObject parent, string name = "") where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (string.IsNullOrEmpty(name) || (child as FrameworkElement)?.Name == name))
                return t;
            var result = FindVisualChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
