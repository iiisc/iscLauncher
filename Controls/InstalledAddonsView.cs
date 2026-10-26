using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Controls;

/// <summary>
/// The Installed tab: every addon in a game, one row per GitHub repo it's connected to and the rest by name. Checks the
/// repos for updates, lets the user connect addons to their repos, and asks its host to install the updates picked.
/// </summary>
public sealed class InstalledAddonsView : UserControl
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    private readonly GameEntry _game;
    private readonly AddonUpdateService _service;
    private readonly string _addOnsDir;
    private readonly List<string> _installed;
    private readonly Dictionary<string, string?> _versions;
    private readonly Dictionary<string, string?> _tocRepos;
    private readonly List<AddonSource> _sources;
    private readonly Dictionary<string, AddonUpdate> _results = new(IgnoreCase);
    private readonly HashSet<string> _checking = new(IgnoreCase);
    private readonly HashSet<string> _ticked = new(IgnoreCase);
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = BodyText("");
    private readonly TextBlock _message = WarningText("");
    private readonly CancellationTokenSource _cts = new();
    private readonly Button _updateSelected = TextButton("Update Selected", "PrimaryButtonStyle");
    private readonly Button _updateAll = TextButton("Update All", "SecondaryButtonStyle");
    private readonly Button _checkAgain = TextButton("Check Again", "SecondaryButtonStyle");
    private bool _busy;

    /// <summary>The updates the user picked, for the host to install and then pass to <see cref="MarkInstalled"/>.</summary>
    public event EventHandler<List<AddonUpdate>>? UpdateRequested;

    /// <summary>The game's sources changed: connected, removed, or found to hold other addons. The host saves them.</summary>
    public event EventHandler? SourcesChanged;

    public InstalledAddonsView(GameEntry game, AddonUpdateService service)
    {
        _game = game;
        _service = service;
        _addOnsDir = AddonFiles.GameAddOnsDir(AddonSyncService.GameDir(game));
        _installed = AddonFiles.AddonNames(_addOnsDir);
        _versions = _installed.ToDictionary(n => n, n => AddonFiles.AddonVersion(Path.Combine(_addOnsDir, n)), IgnoreCase);
        _tocRepos = _installed.ToDictionary(n => n, n => AddonFiles.TocGitHubRepo(Path.Combine(_addOnsDir, n)), IgnoreCase);
        _sources = [.. AddonUpdateService.Sources(game)];
        _message.Visibility = Visibility.Collapsed;

        _updateSelected.Click += (_, _) => UpdateRequested?.Invoke(this, Available.Where(u => _ticked.Contains(u.Repo)).ToList());
        // An update made for another client stays out unless it's ticked and updated on its own
        _updateAll.Click += (_, _) => UpdateRequested?.Invoke(this, Available.Where(u => Warning(u) == null).ToList());
        _checkAgain.Click += async (_, _) => await CheckAllAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(_updateSelected);
        buttons.Children.Add(_updateAll);
        buttons.Children.Add(_checkAgain);

        var layout = new Grid { RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroller = new ScrollViewer { Content = _list, Padding = new Thickness(0, 0, 12, 0) };
        foreach (var (element, row) in new (FrameworkElement, int)[] { (_summary, 0), (_message, 1), (buttons, 2), (scroller, 3) })
        {
            Grid.SetRow(element, row);
            layout.Children.Add(element);
        }
        Content = layout;
        Render();
    }

    private bool HasGame => File.Exists(_game.ExecutablePath);

    /// <summary>Checks every connected repo for updates.</summary>
    public Task CheckAllAsync() => HasGame ? CheckAsync([.. _sources]) : Task.CompletedTask;

    /// <summary>Stops any check, for when the tab moves on to another game.</summary>
    public void Close() => _cts.Cancel();

    /// <summary>Shows installed updates as up to date, without downloading them again.</summary>
    public void MarkInstalled(IEnumerable<AddonUpdate> updates)
    {
        foreach (var update in updates)
        {
            _results[update.Repo] = update with { Changed = [] };
            _ticked.Remove(update.Repo);
        }
        Render();
    }

    /// <summary>Disables the buttons while the host runs something that changes the game's files.</summary>
    public bool IsBusy
    {
        set { _busy = value; Render(); }
    }

    private IEnumerable<AddonUpdate> Available =>
        _sources.Select(s => _results.GetValueOrDefault(s.Repo)).OfType<AddonUpdate>().Where(u => u.Changed.Count > 0);

    private static string? Warning(AddonUpdate update) => update.Changed.Select(a => a.Warning).FirstOrDefault(w => w != null);

    private async Task CheckAsync(IReadOnlyList<AddonSource> sources)
    {
        _checking.UnionWith(sources.Select(s => s.Repo));
        Render();
        try
        {
            foreach (var source in sources)
            {
                var update = await _service.CheckAsync(_game, source.Repo, _cts.Token);
                _results[source.Repo] = update;
                if (update.Error == null && !source.Addons.SequenceEqual(update.Installed, IgnoreCase))
                {
                    source.Addons = update.Installed;
                    SaveSources();
                }
                if (update.Changed.Count > 0 && Warning(update) == null)
                    _ticked.Add(source.Repo);
                _checking.Remove(source.Repo);
                Render();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Connects an addon to a repo, if the repo turns out to have it.</summary>
    private async Task ConnectAsync(string repo, List<string> family)
    {
        _message.Visibility = Visibility.Collapsed;
        var source = _sources.FirstOrDefault(s => IgnoreCase.Equals(s.Repo, repo));
        var added = source == null;
        source ??= new AddonSource { Repo = repo };
        if (added)
        {
            _sources.Add(source);
            SaveSources();
        }

        await CheckAsync([source]);
        if (_results.GetValueOrDefault(repo) is not { } update || update.Installed.Intersect(family, IgnoreCase).Any())
            return;

        _message.Text = update.Error != null ? $"⚠ Couldn't connect {repo}: {update.Error.TrimEnd('.')}." : $"⚠ {repo} doesn't have {family[0]}, so it wasn't connected.";
        _message.Visibility = Visibility.Visible;
        if (added)
            Remove(source);
    }

    private void Remove(AddonSource source)
    {
        _sources.Remove(source);
        _results.Remove(source.Repo);
        _ticked.Remove(source.Repo);
        SaveSources();
        Render();
    }

    // Until this, the game's sources are the ones its .toc files name, found afresh each time
    private void SaveSources()
    {
        _game.AddonSources = _sources;
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Render()
    {
        _list.Children.Clear();
        var connected = new HashSet<string>(IgnoreCase);
        foreach (var source in _sources.OrderBy(s => RepoName(s.Repo), IgnoreCase))
        {
            var folders = _installed.Where(n => source.Addons.Contains(n, IgnoreCase)).ToList();
            connected.UnionWith(folders);
            _list.Children.Add(SourceRow(source, folders));
        }

        var families = _installed.Where(n => !connected.Contains(n))
            .GroupBy(AddonFiles.Family, IgnoreCase)
            .Select(g => g.OrderBy(n => n.Length).ThenBy(n => n, IgnoreCase).ToList())
            .ToList();
        foreach (var family in families)
            _list.Children.Add(LocalRow(family));
        if (!HasGame)
            _list.Children.Add(Dim("The game's executable wasn't found, so its addons can't be listed."));
        else if (_installed.Count == 0)
            _list.Children.Add(Dim("No addons are installed in this game."));

        var updates = Available.Count();
        var status = _checking.Count > 0 ? "Checking for updates..."
            : updates > 0 ? $"{updates.Plural("update")} available."
            : _sources.Count > 0 ? "No updates found."
            : "Connect an addon to its GitHub project to get its updates.";
        _summary.Text = $"{(_sources.Count + families.Count).Plural("addon")}, {_sources.Count} connected to GitHub. {status}";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _updateSelected.IsEnabled = !_busy && Available.Any(u => _ticked.Contains(u.Repo));
        _updateAll.IsEnabled = !_busy && Available.Any(u => Warning(u) == null);
        _checkAgain.IsEnabled = !_busy && HasGame && _checking.Count == 0 && _sources.Count > 0;
    }

    private static Button TextButton(string text, string styleKey) => new() { Content = text, Style = Res<Style>(styleKey) };

    private Grid SourceRow(AddonSource source, List<string> folders)
    {
        var update = _results.GetValueOrDefault(source.Repo);
        var hasUpdate = update?.Changed.Count > 0;

        // The addon that names the repo, or that the repo is named after, says the most (DBM-Core, or WeakAuras rather than its APIDocumentation)
        var lead = folders.OrderByDescending(f => IgnoreCase.Equals(_tocRepos[f], source.Repo))
            .ThenByDescending(f => source.Repo.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .FirstOrDefault();
        var bumped = update?.Changed
            .Where(a => a.SourceVersion != null && a.TargetVersion != null && a.SourceVersion != a.TargetVersion)
            .OrderByDescending(a => IgnoreCase.Equals(a.Name, lead))
            .FirstOrDefault();
        var version = bumped != null ? $"{bumped.TargetVersion} → {bumped.SourceVersion}" : lead == null ? null : _versions[lead];

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(version))
            details.Add(version);
        if (folders.Count > 1)
            details.Add(folders.Count.Plural("folder"));
        if (hasUpdate && update!.Release == null)
            details.Add("newest code");
        // A narrow window shortens the details rather than cutting them off
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.Children.Add(BodyText(RepoName(source.Repo)));
        var detailsText = Dim(string.Join(" · ", details));
        detailsText.TextWrapping = TextWrapping.NoWrap;
        detailsText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(detailsText, 1);
        title.Children.Add(detailsText);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(new HyperlinkButton
        {
            Content = $"github.com/{source.Repo}",
            NavigateUri = new Uri($"https://github.com/{source.Repo}"),
            Padding = new Thickness(0),
            FontSize = 12,
            Foreground = Res<Brush>("GoldBrush")
        });
        if (update?.Error != null)
            text.Children.Add(WarningText($"⚠ {update.Error}"));
        else if (hasUpdate && Warning(update!) is { } warning)
            text.Children.Add(WarningText($"⚠ {warning}. Tick it to update it anyway."));
        if (folders.Count > 1)
            ToolTipService.SetToolTip(text, string.Join("\n", folders));

        UIElement status = _checking.Contains(source.Repo) ? Checking()
            : update == null ? Dim("Not checked")
            : update.Error != null ? Status("Couldn't check", "CrimsonBrush")
            : hasUpdate ? Status("Update available", "GoldBrush")
            : update.Installed.Count == 0 ? Dim("Not installed")
            : Status("Up to date", "EmeraldForegroundBrush");

        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8), Style = Res<Style>("SecondaryButtonStyle") };
        ToolTipService.SetToolTip(remove, "Stop updating from this repo");
        AutomationProperties.SetName(remove, $"Disconnect {source.Repo}");
        remove.Click += (_, _) => Remove(source);

        CheckBox? tick = null;
        if (hasUpdate)
        {
            tick = new CheckBox { IsChecked = _ticked.Contains(source.Repo), MinWidth = 0 };
            AutomationProperties.SetName(tick, $"Update {RepoName(source.Repo)}");
            tick.Checked += (_, _) => { _ticked.Add(source.Repo); UpdateButtons(); };
            tick.Unchecked += (_, _) => { _ticked.Remove(source.Repo); UpdateButtons(); };
        }
        return Row(tick, text, status, remove);
    }

    private Grid LocalRow(List<string> family)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(BodyText(family[0]));
        if (_versions[family[0]] is { Length: > 0 } version)
            title.Children.Add(Dim(version));
        if (family.Count > 1)
            title.Children.Add(Dim($"and {(family.Count - 1).Plural("more part")}"));

        var suggested = family.Select(n => _tocRepos[n]).FirstOrDefault(r => r != null);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(Dim(suggested == null ? "Not connected to GitHub" : $"Not connected · its .toc names github.com/{suggested}"));
        if (family.Count > 1)
            ToolTipService.SetToolTip(text, string.Join("\n", family));
        return Row(null, text, null, ConnectButton(family, suggested));
    }

    private Button ConnectButton(List<string> family, string? suggested)
    {
        var box = new TextBox
        {
            PlaceholderText = "https://github.com/owner/repo",
            Text = suggested == null ? "" : $"https://github.com/{suggested}",
            Width = 360,
            Style = Res<Style>("InputStyle")
        };
        AutomationProperties.SetName(box, $"GitHub repo for {family[0]}");
        var error = WarningText("");
        error.Visibility = Visibility.Collapsed;
        var connect = new Button { Content = "Connect", Style = Res<Style>("PrimaryButtonStyle") };

        var panel = new StackPanel { Spacing = 8, MaxWidth = 380 };
        panel.Children.Add(BodyText($"Paste the link to {family[0]}'s project on GitHub."));
        panel.Children.Add(Dim("Searching GitHub for the addon's name and your game version (such as 3.3.5) usually finds it. " +
            "If the project holds other addons you have too, they're connected with it."));
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(connect);
        var flyout = new Flyout { Content = panel };

        connect.Click += async (_, _) =>
        {
            if (AddonFiles.GitHubRepo(box.Text) is not { } repo)
            {
                error.Text = "⚠ That isn't a link to a GitHub repo.";
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            await ConnectAsync(repo, family);
        };

        var button = new Button { Content = "Connect", Style = Res<Style>("SecondaryButtonStyle"), Flyout = flyout };
        AutomationProperties.SetName(button, $"Connect {family[0]}");
        return button;
    }

    private static Grid Row(CheckBox? tick, UIElement text, UIElement? status, UIElement action)
    {
        var grid = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(0, 8, 0, 8),
            BorderBrush = Res<Brush>("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Add(grid, tick, 0);
        Add(grid, text, 1);
        Add(grid, status, 2);
        Add(grid, action, 3);
        return grid;
    }

    private static void Add(Grid grid, UIElement? element, int column)
    {
        if (element is not FrameworkElement fe) return;
        fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(fe, column);
        grid.Children.Add(fe);
    }

    private static TextBlock Status(string text, string brushKey)
    {
        var block = Dim(text);
        block.Foreground = Res<Brush>(brushKey);
        return block;
    }

    private static StackPanel Checking()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        panel.Children.Add(Dim("Checking"));
        return panel;
    }

    private static string RepoName(string repo) => repo[(repo.IndexOf('/') + 1)..];
}
