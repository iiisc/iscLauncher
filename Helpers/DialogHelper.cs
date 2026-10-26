using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
            RequestedTheme = ElementTheme.Dark,
            PrimaryButtonStyle = Res<Style>("PrimaryButtonStyle")
        };
        // The close and secondary buttons have no style property of their own
        dialog.Resources[typeof(Button)] = new Style(typeof(Button)) { BasedOn = Res<Style>("SecondaryButtonStyle") };
        dialog.Resources["ContentDialogBackground"] = Res<Brush>("Surface2Brush");
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Colors.Transparent);
        dialog.Resources["ContentDialogBorderWidth"] = new Thickness(1);
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = Res<Brush>("BorderBrush");
        dialog.Resources["ContentDialogTitleForeground"] = Res<Brush>("TextPrimaryBrush");
        return dialog;
    }

    /// <summary>Shows a message with a primary button, an optional secondary one and a close button.</summary>
    public static async Task<ContentDialogResult> AskAsync(XamlRoot xamlRoot, string title, string message, string primaryText,
        string? secondaryText = null, string closeText = "Cancel")
    {
        var dialog = CreateThemedDialog(xamlRoot, title);
        dialog.Content = message;
        dialog.PrimaryButtonText = primaryText;
        if (secondaryText != null)
            dialog.SecondaryButtonText = secondaryText;
        dialog.CloseButtonText = closeText;
        return await dialog.ShowAsync();
    }

    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    public static TextBlock StyledText(string text, string styleKey) => new() { Text = text, Style = Res<Style>(styleKey) };

    public static TextBlock BodyText(string text, string? brushKey = null) => Colored(StyledText(text, "BodyTextStyle"), brushKey);

    public static TextBlock Dim(string text, string? brushKey = null)
    {
        var block = Colored(StyledText(text, "HintTextStyle"), brushKey);
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    private static TextBlock Colored(TextBlock block, string? brushKey)
    {
        if (brushKey != null)
            block.Foreground = Res<Brush>(brushKey);
        return block;
    }

    public static Button StyledButton(object content, string styleKey = "SecondaryButtonStyle") =>
        new() { Content = content, Style = Res<Style>(styleKey) };

    /// <summary>A gold link. Without a URL, the caller handles its Click.</summary>
    public static HyperlinkButton Link(string text, string? url = null) =>
        new() { Content = text, NavigateUri = url == null ? null : new Uri(url), Padding = new Thickness(0), Foreground = Res<Brush>("GoldBrush") };

    public static StackPanel Panel(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    public static TextBlock WarningText(string text) => StyledText(text, "WarningTextStyle");

    public static TextBlock SectionLabel(string text, string brushKey = "GoldDarkBrush")
    {
        var block = Colored(StyledText(text, "SectionHeaderStyle"), brushKey);
        block.Margin = new Thickness(0, 4, 0, 0);
        return block;
    }

    public static CheckBox Option(string text, bool isChecked)
    {
        var box = new CheckBox { IsChecked = isChecked, Content = BodyText(text) };
        // A checkbox whose content isn't plain text has no name for screen readers otherwise
        AutomationProperties.SetName(box, text);
        return box;
    }

    public static void SetWidth(ContentDialog dialog, double min, double max)
    {
        dialog.MinWidth = min;
        dialog.MaxWidth = max;
        dialog.Resources["ContentDialogMinWidth"] = min;
        dialog.Resources["ContentDialogMaxWidth"] = max;
    }

}
