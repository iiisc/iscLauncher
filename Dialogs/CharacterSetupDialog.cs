using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

public sealed record CharacterSetupChoice(CharacterFolder Source, List<CharacterFolder> Targets, bool EnabledAddons, bool AddonData);

/// <summary>Picks a character whose addon setup is copied, and the characters (in any game) that receive it.</summary>
public static class CharacterSetupDialog
{
    public static async Task<CharacterSetupChoice?> ShowAsync(XamlRoot xamlRoot, IReadOnlyList<CharacterFolder> sources, IReadOnlyList<CharacterFolder> targets)
    {
        var sourceBox = new ComboBox
        {
            ItemsSource = sources.Select(c => c.Label).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dataSummary = new TextBlock
        {
            FontFamily = Res<FontFamily>("BodyFont"),
            FontSize = 11,
            Foreground = Res<Brush>("TextDimBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        var enabledBox = Option("Which addons are switched on (AddOns.txt)", true);
        var dataBox = Option("Per-character addon data", true);

        var targetRows = new List<(CheckBox Box, CharacterFolder Character)>();
        var targetPanel = new StackPanel { Spacing = 0 };
        foreach (var game in targets.GroupBy(t => t.Game))
        {
            targetPanel.Children.Add(SectionLabel(game.Key.Name.ToUpperInvariant(), "TextMutedBrush"));
            foreach (var character in game)
            {
                var box = Option(character.Label, false);
                targetRows.Add((box, character));
                targetPanel.Children.Add(box);
            }
        }

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BodyText("Give other characters the same addon setup as one character. " +
            "Keybinds, macros and UI layouts are already shared by the whole account, so they aren't copied here. " +
            "The receiving games' settings are backed up first."));
        panel.Children.Add(SectionLabel("FROM"));
        panel.Children.Add(sourceBox);
        panel.Children.Add(dataSummary);
        panel.Children.Add(SectionLabel("COPY"));
        panel.Children.Add(enabledBox);
        panel.Children.Add(dataBox);
        panel.Children.Add(SectionLabel("TO"));
        panel.Children.Add(new ScrollViewer { MaxHeight = 240, Content = targetPanel });

        var dialog = CreateThemedDialog(xamlRoot, "⚔ Copy Character Setup");
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.PrimaryButtonText = "Copy";
        SetWidth(dialog, 520, 640);

        CharacterFolder Source() => sources[sourceBox.SelectedIndex];

        void Refresh()
        {
            var source = Source();
            var data = AddonFiles.CharacterAddonData(source.Dir);
            dataSummary.Text = data.Count == 0
                ? $"{source.Label} has no per-character addon data."
                : $"Per-character data for: {string.Join(", ", data)}";
            foreach (var (box, character) in targetRows)
            {
                // A character can't receive its own setup
                var isSource = character.Dir == source.Dir;
                box.IsEnabled = !isSource;
                if (isSource)
                    box.IsChecked = false;
            }
            dialog.IsPrimaryButtonEnabled = targetRows.Any(r => r.Box.IsChecked == true);
        }

        sourceBox.SelectionChanged += (_, _) => Refresh();
        foreach (var (box, _) in targetRows)
            box.Click += (_, _) => Refresh();
        Refresh();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        return new CharacterSetupChoice(Source(), targetRows.Where(r => r.Box.IsChecked == true).Select(r => r.Character).ToList(),
            enabledBox.IsChecked == true, dataBox.IsChecked == true);
    }
}
