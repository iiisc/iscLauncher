using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Dialogs;

/// <summary>
/// Confirmation dialog shown before pushing addons to the sync repo.
/// Lets the user pick a source character whose settings become the template.
/// </summary>
public static class SyncPushDialog
{
    /// <summary>
    /// Shows the push dialog. Returns the selected character folder path, or <c>null</c> if cancelled.
    /// </summary>
    public static async Task<string?> ShowAsync(
        XamlRoot xamlRoot,
        string wtfAccountDir,
        List<string> characterPaths)
    {
        var charComboBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceBrush"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Height = 40,
            FontSize = 12
        };
        foreach (var charPath in characterPaths)
        {
            var relativePath = Path.GetRelativePath(wtfAccountDir, charPath);
            charComboBox.Items.Add(new ComboBoxItem
            {
                Content = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                Tag = charPath
            });
        }
        charComboBox.SelectedIndex = 0;

        var contentPanel = new StackPanel { Spacing = 12 };

        contentPanel.Children.Add(new TextBlock
        {
            Text = "Select the character whose settings will become the template for all characters.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 13,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        var pickerSection = new StackPanel { Spacing = 4 };
        pickerSection.Children.Add(new TextBlock
        {
            Text = "Source Character",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 10,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMutedBrush"]
        });
        pickerSection.Children.Add(charComboBox);
        contentPanel.Children.Add(pickerSection);

        contentPanel.Children.Add(new TextBlock
        {
            Text = "⚠ This will overwrite the repo with your current local addons and the selected character's settings.",
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
            FontSize = 11,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GoldBrush"],
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = DialogHelper.CreateThemedDialog(xamlRoot, "⚔ Push to Repo");
        dialog.Content = contentPanel;
        dialog.PrimaryButtonText = "Push";

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        if (charComboBox.SelectedItem is not ComboBoxItem selected)
            return null;

        return selected.Tag as string;
    }
}
