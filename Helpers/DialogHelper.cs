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

    public static TextBlock StyledText(string text, string styleKey) => new() { Text = text, Style = Res<Style>(styleKey) };

    public static TextBlock BodyText(string text) => StyledText(text, "BodyTextStyle");

    public static TextBlock Dim(string text)
    {
        var block = StyledText(text, "HintTextStyle");
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    public static TextBlock WarningText(string text) => StyledText(text, "WarningTextStyle");

    public static TextBlock SectionLabel(string text, string brushKey = "GoldDarkBrush")
    {
        var block = StyledText(text, "SectionHeaderStyle");
        block.Foreground = Res<Brush>(brushKey);
        block.Margin = new Thickness(0, 4, 0, 0);
        return block;
    }

    public static CheckBox Option(string text, bool isChecked) => new() { IsChecked = isChecked, Content = BodyText(text) };

    public static void SetWidth(ContentDialog dialog, double min, double max)
    {
        dialog.MinWidth = min;
        dialog.MaxWidth = max;
        dialog.Resources["ContentDialogMinWidth"] = min;
        dialog.Resources["ContentDialogMaxWidth"] = max;
    }

    private static void ApplyTheme(ContentDialog dialog)
    {
        dialog.PrimaryButtonStyle = Res<Style>("PrimaryButtonStyle");

        // Default implicit button style — matches SecondaryButtonStyle for close/cancel buttons.
        dialog.Resources[typeof(Button)] = new Style(typeof(Button)) { BasedOn = Res<Style>("SecondaryButtonStyle") };

        dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["Surface2Brush"];
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Colors.Transparent);
        dialog.Resources["ContentDialogBorderWidth"] = new Thickness(1);
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = Application.Current.Resources["BorderBrush"];
        dialog.Resources["ContentDialogTitleForeground"] = Application.Current.Resources["TextPrimaryBrush"];
    }
}
