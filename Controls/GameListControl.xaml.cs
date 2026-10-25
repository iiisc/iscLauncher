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
            if ((GameListView.ContainerFromItem(item) as ListViewItem)?.ContentTemplateRoot is not Border border)
                continue;
            bool isSelected = item == GameListView.SelectedItem;
            if (isSelected)
            {
                border.BorderBrush = DialogHelper.Res<Brush>("GoldBrush");
                border.BorderThickness = new Thickness(2);
                border.Background = DialogHelper.Res<Brush>("Surface3Brush");
                border.Shadow = new ThemeShadow();
                border.Translation = new Vector3(0, 0, 8);
            }
            else
            {
                border.BorderBrush = DialogHelper.Res<Brush>("BorderBrush");
                border.BorderThickness = new Thickness(1);
                border.Background = DialogHelper.Res<Brush>("Surface2Brush");
                border.Shadow = null;
                border.Translation = new Vector3(0, 0, 0);
            }
        }
    }

    public void SetAddGameHighlight(bool selected) =>
        AddGameButton.Foreground = ApplyHighlight(AddGameButton, selected);

    public void SetOptionsHighlight(bool selected) =>
        OptionsButtonIcon.Foreground = ApplyHighlight(OptionsButton, selected);

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
}
