using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>Shows what a check for addon updates found, one row per GitHub repo, and lets the user pick which to install.</summary>
public static class AddonUpdatesDialog
{
    /// <returns>The updates to install, or null if the user cancelled.</returns>
    public static async Task<List<AddonUpdate>?> ShowAsync(XamlRoot xamlRoot, GameEntry game, List<AddonUpdate> updates)
    {
        var available = updates.Where(u => u.Changed.Count > 0).ToList();
        var rows = new List<(CheckBox Box, AddonUpdate Update)>();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BodyText(available.Count == 0
            ? "No updates found."
            : "Pick what to update. The addons it replaces are backed up first." +
              (game.HasSyncRepo ? " Push afterwards to send the update to your other PCs." : "")));

        if (available.Count > 0)
        {
            panel.Children.Add(SectionLabel("↻  UPDATE AVAILABLE", "EmeraldForegroundBrush"));
            foreach (var update in available)
            {
                var warning = update.Changed.Select(a => a.Warning).FirstOrDefault(w => w != null);
                var box = new CheckBox { IsChecked = warning == null, Content = RowContent(update, warning), Padding = new Thickness(6, 4, 0, 0) };
                AutomationProperties.SetName(box, update.Repo);
                ToolTipService.SetToolTip(box, string.Join("\n", update.Changed.Select(a => a.Name)));
                rows.Add((box, update));
                panel.Children.Add(box);
            }
        }

        var others = updates.Except(available).ToList();
        if (others.Count > 0)
        {
            panel.Children.Add(SectionLabel("NOTHING TO UPDATE"));
            foreach (var update in others)
            {
                panel.Children.Add(update.Error != null
                    ? WarningText($"⚠ {update.Repo}: {update.Error}")
                    : Dim($"{update.Repo}: {(update.UpToDate > 0 ? "up to date" : "none of its addons are installed")}"));
            }
        }

        var dialog = CreateThemedDialog(xamlRoot, $"⚔ Addon Updates for {game.Name}");
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 420 };
        if (available.Count > 0)
            dialog.PrimaryButtonText = "Update";
        else
            dialog.CloseButtonText = "Close";
        SetWidth(dialog, 560, 680);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return rows.Where(r => r.Box.IsChecked == true).Select(r => r.Update).ToList();
    }

    private static StackPanel RowContent(AddonUpdate update, string? warning)
    {
        // Addons that publish only code often leave their version alone, so a version change is shown only when there is
        // one, from the addon the repo is named after if that changed (WeakAuras rather than its APIDocumentation)
        var bumped = update.Changed
            .Where(a => a.SourceVersion != null && a.TargetVersion != null && a.SourceVersion != a.TargetVersion)
            .OrderByDescending(a => update.Repo.Contains(a.Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        var details = new List<string>();
        if (bumped != null)
            details.Add($"{bumped.Name} {bumped.TargetVersion} → {bumped.SourceVersion}");
        if (update.Release == null)
            details.Add("newest code");
        details.Add($"{update.Changed.Count.Plural("addon folder")} changed");

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(BodyText(update.Repo));
        content.Children.Add(Dim(string.Join(" · ", details)));
        if (warning != null)
            content.Children.Add(WarningText($"⚠ {warning}"));
        return content;
    }
}
