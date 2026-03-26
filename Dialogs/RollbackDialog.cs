using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Dialogs;

/// <summary>
/// Confirmation dialog that lets the user pick a commit to roll back to.
/// Shows a commit list with addon detail for the selected entry.
/// </summary>
public static class RollbackDialog
{
    /// <summary>
    /// Shows the rollback dialog. Returns the selected commit, or <c>null</c> if cancelled.
    /// The dialog opens immediately with a spinner while commits are loaded in the background.
    /// </summary>
    public static async Task<GitCommitEntry?> ShowAsync(
        XamlRoot xamlRoot,
        Func<Task<List<GitCommitEntry>>> loadCommitsAsync)
    {
        // Loading indicator shown while commits are fetched
        var loadingRing = new ProgressRing
        {
            IsActive = true,
            Width = 32,
            Height = 32,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"]
        };
        var loadingPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            MinHeight = 150,
            Children =
            {
                loadingRing,
                new TextBlock
                {
                    Text = "Loading commit history\u2026",
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                    FontSize = 13,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };

        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(loadingPanel);

        var dialog = DialogHelper.CreateThemedDialog(xamlRoot, "⚔ Rollback to Previous Sync");
        dialog.Content = contentPanel;
        dialog.PrimaryButtonText = "Rollback";
        dialog.IsPrimaryButtonEnabled = false;
        dialog.MinWidth = 600;
        dialog.MaxWidth = 700;
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;
        dialog.Resources["ContentDialogMinWidth"] = 600.0;

        ListView? commitListView = null;

        dialog.Opened += async (_, _) =>
        {
            List<GitCommitEntry> commits;
            try
            {
                commits = await loadCommitsAsync();
            }
            catch
            {
                loadingRing.IsActive = false;
                loadingPanel.Children.Clear();
                loadingPanel.Children.Add(new TextBlock
                {
                    Text = "Failed to load commit history.",
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                    FontSize = 13,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CrimsonBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            if (commits.Count == 0)
            {
                loadingRing.IsActive = false;
                loadingPanel.Children.Clear();
                loadingPanel.Children.Add(new TextBlock
                {
                    Text = "No commits found in the repo.",
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                    FontSize = 13,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            // Build commit list
            commitListView = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 250,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4)
            };

            // Remove the default blue selection indicator in all visual states
            var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            commitListView.Resources["ListViewItemSelectionIndicatorBrush"] = transparent;
            commitListView.Resources["ListViewItemSelectionIndicatorPointerOverBrush"] = transparent;
            commitListView.Resources["ListViewItemSelectionIndicatorPressedBrush"] = transparent;
            commitListView.Resources["ListViewItemSelectionIndicatorDisabledBrush"] = transparent;

            foreach (var commit in commits)
            {
                var dateDisplay = "";
                if (DateTimeOffset.TryParse(commit.DateString, out var dto))
                    dateDisplay = dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                else if (!string.IsNullOrEmpty(commit.DateString))
                    dateDisplay = commit.DateString;

                var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                headerPanel.Children.Add(new TextBlock
                {
                    Text = commit.Hash,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (!string.IsNullOrEmpty(dateDisplay))
                {
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = dateDisplay,
                        FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                        FontSize = 10,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextDimBrush"],
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                var item = new ListViewItem
                {
                    Tag = commit,
                    Padding = new Thickness(8, 6, 8, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 2,
                        Children =
                        {
                            headerPanel,
                            new TextBlock
                            {
                                Text = commit.Message,
                                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                                FontSize = 12,
                                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                };
                commitListView.Items.Add(item);
            }
            commitListView.SelectedIndex = 0;

            // Addon detail panel — shown below the list when a commit is selected
            var addonDetailHeader = new TextBlock
            {
                Text = "📦 Included Addons",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
                FontSize = 10,
                CharacterSpacing = 100,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldDarkBrush"],
                Visibility = Visibility.Collapsed
            };

            var addonDetailText = new TextBlock
            {
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            var addonDetailScroller = new ScrollViewer
            {
                MaxHeight = 120,
                Content = addonDetailText,
                Visibility = Visibility.Collapsed
            };

            void UpdateAddonDetail(GitCommitEntry c)
            {
                var addons = DialogHelper.ParseAddonListFromBody(c.Body);
                if (addons.Count > 0)
                {
                    addonDetailHeader.Visibility = Visibility.Visible;
                    addonDetailScroller.Visibility = Visibility.Visible;
                    addonDetailText.Visibility = Visibility.Visible;
                    addonDetailText.Text = string.Join(",  ", addons);
                }
                else
                {
                    addonDetailHeader.Visibility = Visibility.Collapsed;
                    addonDetailScroller.Visibility = Visibility.Collapsed;
                }
            }

            // Show detail for the initially selected commit
            UpdateAddonDetail(commits[0]);

            commitListView.SelectionChanged += (_, _) =>
            {
                if (commitListView.SelectedItem is ListViewItem sel && sel.Tag is GitCommitEntry c)
                    UpdateAddonDetail(c);
            };

            contentPanel.Children.Clear();
            contentPanel.Children.Add(new TextBlock
            {
                Text = "Select the commit to restore. This will reset the repo to that snapshot and push.",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 13,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            contentPanel.Children.Add(commitListView);
            contentPanel.Children.Add(addonDetailHeader);
            contentPanel.Children.Add(addonDetailScroller);
            contentPanel.Children.Add(new TextBlock
            {
                Text = "⚠ The repo will be overwritten. Run Pull afterwards to apply the restored state locally.",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap
            });

            dialog.IsPrimaryButtonEnabled = true;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        if (commitListView?.SelectedItem is not ListViewItem selectedItem ||
            selectedItem.Tag is not GitCommitEntry selectedCommit)
            return null;

        return selectedCommit;
    }
}
