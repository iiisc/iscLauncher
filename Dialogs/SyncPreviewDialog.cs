using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

public sealed record SyncChoice(List<AddonDiff> Addons, SyncOptions Options);

/// <summary>Shows what a pull, push or rollback would change and lets the user pick which parts to apply.</summary>
public static class SyncPreviewDialog
{
    private static readonly AddonChecklist.Labels ToThisPc = new(
        "✚  NEW — INSTALL ON THIS PC", "↻  CHANGED — UPDATE ON THIS PC", "✖  NOT IN REPO — REMOVE FROM THIS PC");

    private static readonly AddonChecklist.Labels ToRepo = new(
        "✚  NEW — ADD TO REPO", "↻  CHANGED — UPDATE IN REPO", "✖  NOT ON THIS PC — REMOVE FROM REPO");

    public static async Task<SyncChoice?> ShowAsync(XamlRoot xamlRoot, string title, string primaryText, string intro,
        string? warning, AddonPreview preview, bool toRepo)
    {
        var checklist = new AddonChecklist(preview, toRepo ? ToRepo : ToThisPc);
        var addonSettingsBox = Option("Addon settings — saved settings for all addons, shared by every character", true);
        var characterBox = Option("Character settings — layouts, macros, keybinds and per-character addon data" +
            (preview.Characters > 0 ? $" ({preview.Characters.Plural("character")})" : ""), false);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BodyText(intro));
        if (warning != null)
            panel.Children.Add(WarningText(warning));
        panel.Children.Add(SectionLabel("WHAT TO SYNC"));
        panel.Children.Add(addonSettingsBox);
        panel.Children.Add(characterBox);
        panel.Children.Add(SectionLabel("ADDONS"));
        panel.Children.Add(checklist.View);

        var dialog = CreateThemedDialog(xamlRoot, title);
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.PrimaryButtonText = primaryText;
        SetWidth(dialog, 560, 680);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        return new SyncChoice(
            checklist.Selected,
            new SyncOptions(addonSettingsBox.IsChecked == true, characterBox.IsChecked == true));
    }
}
