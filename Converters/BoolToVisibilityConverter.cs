using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace iscLauncher.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Static helper for x:Bind function binding in DataTemplates.
    /// </summary>
    public static Visibility ToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
