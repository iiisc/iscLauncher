using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
            Background = Res<Brush>("Surface2Brush"),
            Foreground = Res<Brush>("TextPrimaryBrush"),
            BorderBrush = Res<Brush>("BorderHighBrush"),
            CornerRadius = new CornerRadius(8),
            RequestedTheme = ElementTheme.Dark
        };
        ApplyTheme(dialog);
        return dialog;
    }

    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    public static TextBlock BodyText(string text) => new()
    {
        Text = text,
        FontFamily = Res<FontFamily>("BodyFont"),
        FontSize = 13,
        Foreground = Res<Brush>("TextPrimaryBrush"),
        TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock Dim(string text) => new()
    {
        Text = text,
        FontFamily = Res<FontFamily>("BodyFont"),
        FontSize = 11,
        Foreground = Res<Brush>("TextDimBrush"),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static TextBlock WarningText(string text) => new()
    {
        Text = text,
        FontFamily = Res<FontFamily>("BodyFont"),
        FontSize = 11,
        Foreground = Res<Brush>("GoldBrush"),
        FontStyle = Windows.UI.Text.FontStyle.Italic,
        TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock SectionLabel(string text, string brushKey = "GoldDarkBrush") => new()
    {
        Text = text,
        FontFamily = Res<FontFamily>("DisplayFont"),
        FontSize = 10,
        CharacterSpacing = 60,
        Foreground = Res<Brush>(brushKey),
        Margin = new Thickness(0, 4, 0, 0)
    };

    public static CheckBox Option(string text, bool isChecked) => new()
    {
        IsChecked = isChecked,
        Content = new TextBlock
        {
            Text = text,
            FontFamily = Res<FontFamily>("BodyFont"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        }
    };

    public static void SetWidth(ContentDialog dialog, double min, double max)
    {
        dialog.MinWidth = min;
        dialog.MaxWidth = max;
        dialog.Resources["ContentDialogMinWidth"] = min;
        dialog.Resources["ContentDialogMaxWidth"] = max;
    }

    private static void ApplyTheme(ContentDialog dialog)
    {
        // Primary button — emerald green gradient matching PrimaryButtonStyle
        var primaryStyle = new Style(typeof(Button));
        primaryStyle.Setters.Add(new Setter(Button.BackgroundProperty,
            new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Res<Windows.UI.Color>("EmeraldColor"), Offset = 0 },
                    new GradientStop { Color = Res<Windows.UI.Color>("EmeraldDarkColor"), Offset = 1 }
                }
            }));
        primaryStyle.Setters.Add(new Setter(Button.ForegroundProperty, Res<Brush>("EmeraldForegroundBrush")));
        primaryStyle.Setters.Add(new Setter(Button.BorderBrushProperty,
            new SolidColorBrush { Color = Res<Windows.UI.Color>("EmeraldLightColor"), Opacity = 0.3 }));
        primaryStyle.Setters.Add(new Setter(Button.CornerRadiusProperty, new CornerRadius(6)));
        primaryStyle.Setters.Add(new Setter(Button.FontWeightProperty, new Windows.UI.Text.FontWeight(700)));
        dialog.PrimaryButtonStyle = primaryStyle;

        // Default implicit button style — matches SecondaryButtonStyle for close/cancel buttons.
        dialog.Resources[typeof(Button)] = new Style(typeof(Button)) { BasedOn = Res<Style>("SecondaryButtonStyle") };

        dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["Surface2Brush"];
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Colors.Transparent);
        dialog.Resources["ContentDialogBorderWidth"] = new Thickness(1);
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = Application.Current.Resources["BorderBrush"];
        dialog.Resources["ContentDialogTitleForeground"] = Application.Current.Resources["TextPrimaryBrush"];
    }
}
