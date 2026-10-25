using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

public sealed record CopyChoice(GameEntry Target, List<AddonDiff> Addons, bool CopySettings);

/// <summary>Picks a target game and the addons to copy into it from <c>source</c>.</summary>
public static class CopyAddonsDialog
{
    public static async Task<CopyChoice?> ShowAsync(XamlRoot xamlRoot, GameEntry source, IReadOnlyList<GameEntry> targets,
        Func<GameEntry, CancellationToken, Task<AddonPreview>> previewAsync)
    {
        var targetBox = new ComboBox
        {
            ItemsSource = targets.Select(g => g.Name).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var settingsBox = Option("Also copy their account-wide settings (SavedVariables)", true);
        var listHost = new Border { MinHeight = 120 };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BodyText($"Copy addons from {source.Name} into another game. " +
            "Addons that get replaced, and the target's settings, are backed up first."));
        panel.Children.Add(SectionLabel("COPY TO"));
        panel.Children.Add(targetBox);
        panel.Children.Add(settingsBox);
        panel.Children.Add(SectionLabel("ADDONS"));
        panel.Children.Add(listHost);

        var dialog = CreateThemedDialog(xamlRoot, $"⚔ Copy Addons from {source.Name}");
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.PrimaryButtonText = "Copy";
        SetWidth(dialog, 560, 680);

        AddonChecklist? checklist = null;
        // Each preview reads every addon on both sides, so one for a target the user has moved past is cancelled
        CancellationTokenSource? previewCts = null;

        async Task LoadPreviewAsync()
        {
            var target = targets[targetBox.SelectedIndex];
            previewCts?.Cancel();
            var cts = previewCts = new CancellationTokenSource();
            checklist = null;
            dialog.IsPrimaryButtonEnabled = false;
            listHost.Child = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };

            AddonPreview preview;
            try
            {
                preview = await previewAsync(target, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                listHost.Child = WarningText($"Couldn't read addons: {ex.Message}");
                return;
            }
            // A preview can finish just as the user picks another game; its result is stale then
            if (cts.IsCancellationRequested)
                return;

            var name = target.Name.ToUpperInvariant();
            checklist = new AddonChecklist(preview, new($"✚  NOT IN {name} — COPY", $"↻  DIFFERENT IN {name} — REPLACE", ""));
            listHost.Child = checklist.View;
            dialog.IsPrimaryButtonEnabled = preview.Diffs.Count > 0;
        }

        targetBox.SelectionChanged += async (_, _) => await LoadPreviewAsync();
        dialog.Opened += async (_, _) => await LoadPreviewAsync();

        var result = await dialog.ShowAsync();
        previewCts?.Cancel();
        if (result != ContentDialogResult.Primary || checklist == null)
            return null;

        return new CopyChoice(targets[targetBox.SelectedIndex], checklist.Selected, settingsBox.IsChecked == true);
    }
}
