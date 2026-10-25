using System.Collections.Generic;
using System.Linq;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>Addon changes grouped by kind, one checkbox each, all ticked to start.</summary>
public sealed class AddonChecklist
{
    public record Labels(string New, string Changed, string Removed);

    private readonly List<(CheckBox Box, AddonDiff Addon)> _rows = [];
    private readonly StackPanel _panel = new() { Spacing = 10 };

    public UIElement View { get; }

    public AddonChecklist(AddonPreview preview, Labels labels)
    {
        AddGroup(preview.Diffs, AddonChange.New, labels.New, "EmeraldForegroundBrush");
        AddGroup(preview.Diffs, AddonChange.Changed, labels.Changed, "TextMutedBrush");
        AddGroup(preview.Diffs, AddonChange.Removed, labels.Removed, "CrimsonBrush");

        var note = (preview.Diffs.Count, preview.Unchanged) switch
        {
            (0, 0) => "No addons found.",
            (0, var same) => $"All {same} addon(s) already match.",
            (_, 0) => null,
            (_, var same) => $"{same} other addon(s) already match."
        };
        if (note != null)
            _panel.Children.Add(Dim(note));

        View = new ScrollViewer { MaxHeight = 280, Content = _panel };
    }

    public List<AddonDiff> Selected => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Addon).ToList();

    public bool IsEnabled
    {
        set
        {
            foreach (var (box, _) in _rows)
                box.IsEnabled = value;
        }
    }

    private void AddGroup(List<AddonDiff> diffs, AddonChange change, string label, string brushKey)
    {
        var addons = diffs.Where(d => d.Change == change).ToList();
        if (addons.Count == 0)
            return;

        var group = new StackPanel { Spacing = 0 };
        group.Children.Add(SectionLabel($"{label} ({addons.Count})", brushKey));
        foreach (var addon in addons)
        {
            var box = new CheckBox { IsChecked = true, Content = RowContent(addon), MinHeight = 28, Padding = new Thickness(6, 4, 0, 0) };
            _rows.Add((box, addon));
            group.Children.Add(box);
        }
        _panel.Children.Add(group);
    }

    private static StackPanel RowContent(AddonDiff addon)
    {
        var version = addon.Change switch
        {
            AddonChange.Changed when addon.TargetVersion != null && addon.SourceVersion != null && addon.TargetVersion != addon.SourceVersion
                => $"{addon.TargetVersion} → {addon.SourceVersion}",
            AddonChange.Removed => addon.TargetVersion,
            _ => addon.SourceVersion
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new TextBlock
        {
            Text = addon.Name,
            FontFamily = Res<FontFamily>("BodyFont"),
            FontSize = 12,
            Foreground = Res<Brush>("TextPrimaryBrush")
        });
        if (!string.IsNullOrWhiteSpace(version))
            line.Children.Add(Dim(version));

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(line);
        if (addon.Warning != null)
            content.Children.Add(WarningText($"⚠ {addon.Warning}"));
        return content;
    }

    private static TextBlock Dim(string text) => new()
    {
        Text = text,
        FontFamily = Res<FontFamily>("BodyFont"),
        FontSize = 11,
        Foreground = Res<Brush>("TextDimBrush"),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
}
