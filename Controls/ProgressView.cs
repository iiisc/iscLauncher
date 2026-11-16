using System;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Controls;

/// <summary>
/// The app's one way of showing work in progress, placed by the button that started it: what's happening, over a bar
/// that fills when it's known how far along it is, and a Cancel button when the work can be stopped.
/// </summary>
public sealed partial class ProgressView : Grid
{
    private readonly TextBlock _text = Dim("");
    private readonly ProgressBar _bar = new()
    {
        Minimum = 0,
        Maximum = 1,
        IsIndeterminate = true,
        CornerRadius = new CornerRadius(4),
        Foreground = Res<Brush>("EmeraldBrush")
    };
    private readonly Button _cancelButton;
    private Action? _cancel;

    public ProgressView() : this("") => Visibility = Visibility.Collapsed;

    /// <summary>A view that's showing <paramref name="text"/> from the start, for a dialog's loading state.</summary>
    public ProgressView(string text)
    {
        RowSpacing = 6;
        ColumnSpacing = 8;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _text.TextWrapping = TextWrapping.Wrap;
        _text.Text = text;
        SetColumnSpan(_text, 2);
        Children.Add(_text);

        _bar.VerticalAlignment = VerticalAlignment.Center;
        SetRow(_bar, 1);
        Children.Add(_bar);

        _cancelButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = Res<Brush>("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_cancelButton, "Cancel");
        ToolTipService.SetToolTip(_cancelButton, "Cancel");
        _cancelButton.Click += (_, _) => _cancel?.Invoke();
        SetRow(_cancelButton, 1);
        SetColumn(_cancelButton, 1);
        Children.Add(_cancelButton);
    }

    /// <summary>Shows the view; <paramref name="cancel"/>, if given, is what its Cancel button does.</summary>
    public void Start(ProgressReport report, Action? cancel = null)
    {
        _cancel = cancel;
        _cancelButton.Visibility = cancel != null ? Visibility.Visible : Visibility.Collapsed;
        Report(report);
        Visibility = Visibility.Visible;
    }

    public void Finish() => Visibility = Visibility.Collapsed;

    /// <summary>For handing to work that reports from other threads; get it on the UI thread.</summary>
    public IProgress<ProgressReport> Reporter => new Progress<ProgressReport>(Report);

    public void Report(ProgressReport value)
    {
        _text.Text = value.Text;
        _bar.IsIndeterminate = value.Fraction == null;
        _bar.Value = value.Fraction ?? 0;
    }
}
