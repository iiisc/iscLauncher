using System.Collections.Generic;
using System.Linq;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>
/// Addon changes grouped by kind, one checkbox per addon family, ticked to start unless flagged as incompatible.
/// Long groups start collapsed so a big sync doesn't bury the rest of the dialog.
/// </summary>
public sealed class AddonChecklist
{
    public record Labels(string New, string Changed, string Removed);

    private const int RowsShownExpanded = 8;

    private readonly List<(CheckBox Box, List<AddonDiff> Family)> _rows = [];
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
            (0, var same) => $"All {same.Plural("addon")} already match.",
            (_, 0) => null,
            (_, var same) => $"{same.Plural("other addon")} already match."
        };
        if (note != null)
            _panel.Children.Add(Dim(note));

        View = new ScrollViewer { MaxHeight = 280, Content = _panel };
    }

    public List<AddonDiff> Selected => _rows.Where(r => r.Box.IsChecked == true).SelectMany(r => r.Family).ToList();

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

        var rows = new StackPanel();
        var anyWarning = false;
        foreach (var family in AddonFiles.Families(addons, a => a.Name))
        {
            var warning = family.Select(a => a.Warning).FirstOrDefault(w => w != null);
            anyWarning |= warning != null;
            var box = new CheckBox { IsChecked = warning == null, Content = RowContent(family, warning), MinHeight = 28, Padding = new Thickness(6, 4, 0, 0) };
            AutomationProperties.SetName(box, family[0].Name);
            if (family.Count > 1)
                ToolTipService.SetToolTip(box, string.Join("\n", family.Select(a => a.Name)));
            _rows.Add((box, family));
            rows.Children.Add(box);
        }

        // A collapsed group would hide which rows were left unticked for a warning, so those stay open
        _panel.Children.Add(new Expander
        {
            Header = SectionLabel($"{label} ({addons.Count})", brushKey),
            Content = rows,
            IsExpanded = rows.Children.Count <= RowsShownExpanded || anyWarning,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        });
    }

    private static StackPanel RowContent(List<AddonDiff> family, string? warning)
    {
        var addon = family[0];
        var version = addon.Change switch
        {
            AddonChange.Changed when addon.VersionChange != null => addon.VersionChange,
            AddonChange.Removed => addon.TargetVersion,
            _ => addon.SourceVersion
        };

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(FamilyTitle(addon.Name, version, family.Count));
        if (warning != null)
            content.Children.Add(WarningText($"⚠ {warning}"));
        return content;
    }

    /// <summary>An addon's name and version, and how many more folders it ships as.</summary>
    internal static StackPanel FamilyTitle(string name, string? version, int folders)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(BodyText(name));
        if (!string.IsNullOrWhiteSpace(version))
            line.Children.Add(Dim(version));
        if (folders > 1)
            line.Children.Add(Dim($"and {(folders - 1).Plural("more part")}"));
        return line;
    }
}
