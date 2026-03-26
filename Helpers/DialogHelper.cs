using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Helpers;

public static class DialogHelper
{
    /// <summary>
    /// Creates a <see cref="ContentDialog"/> pre-configured with the app's dark fantasy theme.
    /// Callers still need to set <c>Content</c>, <c>PrimaryButtonText</c>, and any size overrides.
    /// </summary>
    public static ContentDialog CreateThemedDialog(XamlRoot xamlRoot, string title)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Surface2Brush"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderHighBrush"],
            CornerRadius = new CornerRadius(8),
            RequestedTheme = ElementTheme.Dark
        };
        ApplyTheme(dialog);
        return dialog;
    }

    /// <summary>
    /// Applies the full fantasy theme (button styles, chrome, combo box resources) to a dialog.
    /// </summary>
    public static void ApplyTheme(ContentDialog dialog)
    {
        // Primary button — emerald green gradient matching PrimaryButtonStyle
        var primaryStyle = new Style(typeof(Button));
        primaryStyle.Setters.Add(new Setter(Button.BackgroundProperty,
            new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = (Windows.UI.Color)Application.Current.Resources["EmeraldColor"], Offset = 0 },
                    new Microsoft.UI.Xaml.Media.GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 45, 90, 70), Offset = 1 }
                }
            }));
        primaryStyle.Setters.Add(new Setter(Button.ForegroundProperty,
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 200, 240, 216))));
        primaryStyle.Setters.Add(new Setter(Button.BorderBrushProperty,
            new Microsoft.UI.Xaml.Media.SolidColorBrush { Color = (Windows.UI.Color)Application.Current.Resources["EmeraldLightColor"], Opacity = 0.3 }));
        primaryStyle.Setters.Add(new Setter(Button.CornerRadiusProperty, new CornerRadius(6)));
        primaryStyle.Setters.Add(new Setter(Button.FontWeightProperty, new Windows.UI.Text.FontWeight(700)));
        dialog.PrimaryButtonStyle = primaryStyle;

        // Default implicit button style — matches SecondaryButtonStyle for close/cancel buttons.
        var secondaryBase = (Style)Application.Current.Resources["SecondaryButtonStyle"];
        dialog.Resources[typeof(Button)] = new Style(typeof(Button)) { BasedOn = secondaryBase };

        // Dialog chrome
        dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["Surface2Brush"];
        dialog.Resources["ContentDialogTopOverlay"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Resources["ContentDialogBorderWidth"] = new Thickness(1);
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = Application.Current.Resources["BorderBrush"];

        // Title
        dialog.Resources["ContentDialogTitleForeground"] = Application.Current.Resources["TextPrimaryBrush"];

        // ComboBox dropdown
        dialog.Resources["ComboBoxDropDownBackground"] = Application.Current.Resources["Surface2Brush"];
        dialog.Resources["ComboBoxDropDownBorderBrush"] = Application.Current.Resources["BorderHighBrush"];
        dialog.Resources["ComboBoxItemForeground"] = Application.Current.Resources["TextPrimaryBrush"];
        dialog.Resources["ComboBoxItemForegroundSelected"] = Application.Current.Resources["TextPrimaryBrush"];
        dialog.Resources["ComboBoxItemForegroundPointerOver"] = Application.Current.Resources["GoldLightBrush"];
        dialog.Resources["ComboBoxItemBackgroundPointerOver"] = Application.Current.Resources["Surface3Brush"];
        dialog.Resources["ComboBoxItemBackgroundSelected"] = Application.Current.Resources["Surface3Brush"];
        dialog.Resources["ComboBoxItemBackgroundSelectedPointerOver"] = Application.Current.Resources["Surface3Brush"];
    }

    /// <summary>
    /// Parses an addon list from the body of a git commit message.
    /// Expects a section starting with "Addons:" followed by "- AddonName" lines.
    /// </summary>
    public static List<string> ParseAddonListFromBody(string body)
    {
        var addons = new List<string>();
        if (string.IsNullOrWhiteSpace(body))
            return addons;

        var inAddonSection = false;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals("Addons:", StringComparison.OrdinalIgnoreCase))
            {
                inAddonSection = true;
                continue;
            }

            if (inAddonSection)
            {
                if (line.StartsWith("- "))
                    addons.Add(line[2..].Trim());
                else if (line.Length == 0 && addons.Count > 0)
                    break;
            }
        }
        return addons;
    }
}
