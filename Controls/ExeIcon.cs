using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace iscLauncher.Controls;

/// <summary>An executable's icon, with a placeholder while it loads or when Windows has none.</summary>
public sealed partial class ExeIcon : Grid
{
    // A dependency property so a bound list card follows whichever game it's reused for as games move
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(ExeIcon), new PropertyMetadata(null, (d, e) => _ = ((ExeIcon)d).LoadAsync()));

    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    private readonly FontIcon _placeholder = new()
    {
        Glyph = "",
        FontSize = 20,
        Opacity = 0.5,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Image _image = new()
    {
        Width = 40,
        Height = 40,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public ExeIcon()
    {
        _placeholder.Foreground = DialogHelper.Res<Brush>("GoldBrush");
        Children.Add(_placeholder);
        Children.Add(_image);
    }

    private async Task LoadAsync()
    {
        var path = Path;
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        var icon = await IconExtractor.GetIconFromExecutableAsync(path ?? "");
        // Another icon may have been asked for while this one loaded
        if (path != Path) return;
        _image.Source = icon;
        _placeholder.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
    }
}
