using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using iscLauncher.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>
/// Confirmation dialog that lets the user pick a commit to roll back to.
/// Shows the repo's recent commits.
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
        var loadingRing = new ProgressRing
        {
            IsActive = true,
            Width = 32,
            Height = 32,
            Foreground = Res<Brush>("GoldBrush")
        };
        var loadingPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            MinHeight = 150,
            Children = { loadingRing, CenteredMessage("Loading commit history…", "TextMutedBrush") }
        };

        var contentPanel = new StackPanel { Spacing = 12 };
        contentPanel.Children.Add(loadingPanel);

        var dialog = CreateThemedDialog(xamlRoot, "⚔ Rollback to Previous Sync");
        dialog.Content = contentPanel;
        dialog.PrimaryButtonText = "Next";
        dialog.IsPrimaryButtonEnabled = false;
        SetWidth(dialog, 600, 700);

        ListView? commitListView = null;

        void ShowMessage(string text, string brushKey)
        {
            loadingRing.IsActive = false;
            loadingPanel.Children.Clear();
            loadingPanel.Children.Add(CenteredMessage(text, brushKey));
        }

        dialog.Opened += async (_, _) =>
        {
            List<GitCommitEntry> commits;
            try
            {
                commits = await loadCommitsAsync();
            }
            catch (Exception ex)
            {
                ShowMessage($"Failed to load commit history. {ex.Message}", "CrimsonBrush");
                return;
            }

            if (commits.Count == 0)
            {
                ShowMessage("No commits found in the repo.", "TextMutedBrush");
                return;
            }

            commitListView = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 250,
                Background = Res<Brush>("SurfaceBrush"),
                BorderBrush = Res<Brush>("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4)
            };

            // Remove the default blue selection indicator in all visual states
            var transparent = new SolidColorBrush(Colors.Transparent);
            commitListView.Resources["ListViewItemSelectionIndicatorBrush"] = transparent;
            commitListView.Resources["ListViewItemSelectionIndicatorPointerOverBrush"] = transparent;
            commitListView.Resources["ListViewItemSelectionIndicatorPressedBrush"] = transparent;
            commitListView.Resources["ListViewItemSelectionIndicatorDisabledBrush"] = transparent;

            foreach (var commit in commits)
            {

                var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                headerPanel.Children.Add(new TextBlock
                {
                    Text = commit.Hash,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = Res<Brush>("GoldBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (!string.IsNullOrEmpty(commit.DisplayDate))
                {
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = commit.DisplayDate,
                        FontFamily = Res<FontFamily>("BodyFont"),
                        FontSize = 10,
                        Foreground = Res<Brush>("TextDimBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                commitListView.Items.Add(new ListViewItem
                {
                    Tag = commit,
                    Padding = new Thickness(8, 6, 8, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            headerPanel,
                            new TextBlock
                            {
                                Text = commit.Message,
                                FontFamily = Res<FontFamily>("BodyFont"),
                                FontSize = 12,
                                Foreground = Res<Brush>("TextPrimaryBrush"),
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                });
            }
            commitListView.SelectedIndex = 0;
            contentPanel.Children.Clear();
            contentPanel.Children.Add(BodyText(
                "Select the version to restore. Next you'll see what changes on this PC and choose what to apply."));
            contentPanel.Children.Add(commitListView);

            dialog.IsPrimaryButtonEnabled = true;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        return (commitListView?.SelectedItem as ListViewItem)?.Tag as GitCommitEntry;
    }

    private static TextBlock CenteredMessage(string text, string brushKey) => new()
    {
        Text = text,
        FontFamily = Res<FontFamily>("BodyFont"),
        FontSize = 13,
        Foreground = Res<Brush>(brushKey),
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap
    };
}
