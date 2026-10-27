using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Dialogs;
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
/// The Addons tab: every addon in a game, one row per GitHub repo it's connected to and the rest by name. Checks the
/// repos for updates, lets the user connect addons to their repos, and asks its host to install the updates picked.
/// </summary>
public sealed class InstalledAddonsView : UserControl
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    private readonly GameEntry _game;
    private readonly AddonUpdateService _service;
    private readonly string _addOnsDir;
    private readonly string _downloadDir = AddonUpdateService.NewDownloadDir();
    // Each installed addon's version and the repo its .toc names, read once per listing
    private Dictionary<string, (string? Version, string? Repo)> _toc;
    private readonly List<AddonSource> _sources;
    private readonly Dictionary<string, AddonUpdate> _results = new(IgnoreCase);
    private readonly HashSet<string> _checking = new(IgnoreCase);
    private readonly HashSet<string> _ticked = new(IgnoreCase);
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = BodyText("");
    private readonly TextBlock _message = WarningText("");
    private readonly CancellationTokenSource _cts = new();
    private readonly Button _updateSelected = StyledButton("Update Selected", "PrimaryButtonStyle");
    private readonly Button _updateAll = StyledButton("Update All");
    private bool _busy;
    private Task _installing = Task.CompletedTask;

    /// <summary>Installs the updates the user picked, then <see cref="RescanAsync"/>; set by the host.</summary>
    public required Func<List<AddonUpdate>, Task> InstallAsync { get; init; }

    /// <summary>The game's sources changed: connected, removed, or found to hold other addons. The host saves them.</summary>
    public event EventHandler? SourcesChanged;

    public InstalledAddonsView(GameEntry game, AddonUpdateService service)
    {
        _game = game;
        _service = service;
        _addOnsDir = AddonFiles.GameAddOnsDir(game);
        _toc = AddonFiles.InstalledAddons(_addOnsDir);
        _sources = AddonUpdateService.Sources(game, _toc);
        _message.Visibility = Visibility.Collapsed;

        _updateSelected.Click += (_, _) => Install(Available.Where(u => _ticked.Contains(u.Repo)).ToList());
        // An update made for another client stays out unless it's ticked and updated on its own
        _updateAll.Click += (_, _) => Install(Available.Where(u => Warning(u) == null).ToList());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(_updateSelected);
        buttons.Children.Add(_updateAll);
        var header = new StackPanel { Spacing = 10 };
        header.Children.Add(_summary);
        header.Children.Add(_message);
        header.Children.Add(buttons);

        var layout = new Grid { RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroller = new ScrollViewer { Content = _list, Padding = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(scroller, 1);
        layout.Children.Add(header);
        layout.Children.Add(scroller);
        Content = layout;
        Render();
    }

    private bool HasGame => File.Exists(_game.ExecutablePath);

    /// <summary>Checks every connected repo for updates.</summary>
    public Task CheckAllAsync() => HasGame ? CheckAsync([.. _sources]) : Task.CompletedTask;

    /// <summary>Stops any check and deletes the downloads, for when the tab moves on.</summary>
    public void Close()
    {
        _cts.Cancel();
        // An install keeps going when the user moves to another game, and copies from the downloads until it's done
        _installing.ContinueWith(_ => AddonUpdateService.Discard(_downloadDir), TaskScheduler.Default);
    }

    private async void Install(List<AddonUpdate> picked) => await (_installing = InstallAsync(picked));

    /// <summary>Lists the game's addons again and compares them with the downloads, after something changed them.</summary>
    public async Task RescanAsync()
    {
        var downloaded = _results.Values.Where(u => u.Dir != null).ToList();
        var (toc, compared) = await Task.Run(() =>
            (AddonFiles.InstalledAddons(_addOnsDir), downloaded.Select(u => AddonUpdateService.Compare(_game, u)).ToList()));
        _toc = toc;
        foreach (var update in compared)
        {
            if (Source(update.Repo) is { } source)
                Apply(source, update);
        }
        Render();
        // The rest weren't downloaded because nothing had changed. Checking them again downloads only those whose files
        // the change touched; it runs on its own so the operation that called this isn't kept waiting.
        _ = CheckAsync([.. _sources.Where(s => _results.GetValueOrDefault(s.Repo) is { Dir: null, Error: null })]);
    }

    /// <summary>Disables the buttons while the host runs something that changes the game's files.</summary>
    public bool IsBusy
    {
        set { _busy = value; UpdateButtons(); }
    }

    private AddonSource? Source(string repo) => _sources.FirstOrDefault(s => IgnoreCase.Equals(s.Repo, repo));

    private IEnumerable<AddonUpdate> Available =>
        _sources.Select(s => _results.GetValueOrDefault(s.Repo)).OfType<AddonUpdate>().Where(u => u.Changed.Count > 0);

    private static string? Warning(AddonUpdate update) => update.Changed.Select(a => a.Warning).FirstOrDefault(w => w != null);

    private async Task CheckAsync(IReadOnlyList<AddonSource> sources)
    {
        if (sources.Count == 0)
            return;
        _checking.UnionWith(sources.Select(s => s.Repo));
        Render();
        try
        {
            // The checks download in parallel; each result is shown as it arrives, back on the UI thread
            await Task.WhenAll(sources.Select(async source =>
            {
                Apply(source, await _service.CheckAsync(_game, source, _downloadDir, _cts.Token));
                Render();
            }));
        }
        catch (OperationCanceledException) { }
    }

    private void Apply(AddonSource source, AddonUpdate update)
    {
        var hadUpdate = _results.GetValueOrDefault(source.Repo)?.Changed.Count > 0;
        _results[source.Repo] = update;
        _checking.Remove(source.Repo);
        var matched = update.Error == null && update.Changed.Count == 0;
        if ((update.Error == null && !source.Addons.SequenceEqual(update.Installed, IgnoreCase))
            || (matched && (source.MatchedVersion != update.Latest || source.MatchedFiles != update.Files)))
        {
            source.Addons = update.Installed;
            if (matched)
                (source.MatchedVersion, source.MatchedFiles) = (update.Latest, update.Files);
            SaveSources();
        }
        // Ticked when an update first shows up; after that the user's choice stands
        if (update.Changed.Count == 0)
            _ticked.Remove(source.Repo);
        else if (!hadUpdate && Warning(update) == null)
            _ticked.Add(source.Repo);
    }

    /// <summary>Connects an addon to a repo, if the repo turns out to have it.</summary>
    private async Task ConnectAsync(string repo, List<string> family)
    {
        _message.Visibility = Visibility.Collapsed;
        var source = Source(repo);
        var added = source == null;
        // Saved by the check once it finds the repo's addons
        source ??= new AddonSource { Repo = repo };
        if (added)
            _sources.Add(source);

        await CheckAsync([source]);
        if (_results.GetValueOrDefault(repo) is not { } update)
            return;
        if (update.Installed.Intersect(family, IgnoreCase).Any())
        {
            if (_game.DisconnectedAddonSources?.RemoveAll(r => IgnoreCase.Equals(r, repo)) > 0)
                SaveSources();
            return;
        }

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
        // Otherwise an addon whose .toc names it would connect it again
        _game.DisconnectedAddonSources = [.. (_game.DisconnectedAddonSources ?? []).Append(source.Repo).Distinct(IgnoreCase)];
        SaveSources();
        Render();
    }

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
            var folders = _toc.Keys.Where(n => source.Addons.Contains(n, IgnoreCase)).ToList();
            connected.UnionWith(folders);
            _list.Children.Add(SourceRow(source, folders));
        }

        var families = AddonFiles.Families(_toc.Keys.Where(n => !connected.Contains(n)), n => n).ToList();
        foreach (var family in families)
            _list.Children.Add(LocalRow(family));
        if (!HasGame)
            _list.Children.Add(Dim("The game's executable wasn't found, so its addons can't be listed."));
        else if (_toc.Count == 0)
            _list.Children.Add(Dim("No addons are installed in this game."));

        var updates = Available.Count();
        var status = _checking.Count > 0 ? "Checking for updates..."
            : updates > 0 ? $"{updates.Plural("update")} available."
            : _sources.Count > 0 ? "No updates found."
            : "Connect an addon to its GitHub project to get its updates.";
        _summary.Text = $"{(_sources.Count + families.Count).Plural("addon")}, {_sources.Count} connected to GitHub. {status}";
        if (_checking.Count == 0 && HasGame)
            _game.HasAddonUpdates = updates > 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _updateSelected.IsEnabled = !_busy && Available.Any(u => _ticked.Contains(u.Repo));
        _updateAll.IsEnabled = !_busy && Available.Any(u => Warning(u) == null);
    }

    private Grid SourceRow(AddonSource source, List<string> folders)
    {
        var update = _results.GetValueOrDefault(source.Repo);
        var hasUpdate = update?.Changed.Count > 0;

        // The addon that names the repo, or that the repo is named after, says the most (DBM-Core, or WeakAuras rather than its APIDocumentation)
        var lead = folders.OrderByDescending(f => IgnoreCase.Equals(_toc[f].Repo, source.Repo))
            .ThenByDescending(f => source.Repo.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Length)
            .FirstOrDefault();
        var bumped = update?.Changed
            .Where(a => a.VersionChange != null)
            .OrderByDescending(a => IgnoreCase.Equals(a.Name, lead))
            .FirstOrDefault();
        var version = bumped?.VersionChange ?? (lead == null ? null : _toc[lead].Version);

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
        var link = Link($"github.com/{source.Repo}", $"https://github.com/{source.Repo}");
        link.FontSize = 12;
        text.Children.Add(link);
        if (update?.Error != null)
            text.Children.Add(WarningText($"⚠ {update.Error}"));
        else if (hasUpdate && Warning(update!) is { } warning)
            text.Children.Add(WarningText($"⚠ {warning}. Tick it to update it anyway."));
        if (folders.Count > 1)
            ToolTipService.SetToolTip(text, string.Join("\n", folders));

        UIElement status = _checking.Contains(source.Repo) ? Checking()
            : update == null ? Dim("Not checked")
            : update.Error != null ? Dim("Couldn't check", "CrimsonBrush")
            : hasUpdate ? Dim("Update available", "GoldBrush")
            : update.Installed.Count == 0 ? Dim("Not installed")
            : Dim("Up to date", "EmeraldForegroundBrush");

        var remove = StyledButton(new FontIcon { Glyph = "", FontSize = 12 });
        remove.Padding = new Thickness(8);
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
        var title = AddonChecklist.FamilyTitle(family[0], _toc[family[0]].Version, family.Count);
        var suggested = family.Select(n => _toc[n].Repo).FirstOrDefault(r => r != null);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(Dim(suggested == null ? "Not connected to GitHub" : $"Not connected · its .toc names github.com/{suggested}"));
        if (family.Count > 1)
            ToolTipService.SetToolTip(text, string.Join("\n", family));
        return Row(null, text, null, ConnectButton(family, suggested));
    }

    private Button ConnectButton(List<string> family, string? suggested)
    {
        var button = StyledButton("Connect");
        AutomationProperties.SetName(button, $"Connect {family[0]}");
        // Built when clicked rather than for every unconnected row on every render
        button.Click += (_, _) => ConnectFlyout(family, suggested).ShowAt(button);
        return button;
    }

    private Flyout ConnectFlyout(List<string> family, string? suggested)
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
        var connect = StyledButton("Connect", "PrimaryButtonStyle");

        var matches = new StackPanel { Spacing = 4 };
        matches.Children.Add(Dim("Looking on GitHub..."));

        var panel = new StackPanel { Spacing = 8, MaxWidth = 380 };
        panel.Children.Add(BodyText($"Pick {family[0]}'s project on GitHub, or paste its link."));
        panel.Children.Add(matches);
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(connect);
        panel.Children.Add(Dim("If the project holds other addons you have too, they're connected with it."));
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 480 }, FlyoutPresenterStyle = Res<Style>("FlyoutPresenterStyle") };
        _ = ShowMatchesAsync(matches, family, flyout);

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
        return flyout;
    }

    private async Task ShowMatchesAsync(StackPanel matches, List<string> family, Flyout flyout)
    {
        List<(string Repo, string Description, int Stars)> found;
        try
        {
            found = await _service.FindReposAsync(family[0], _game, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            matches.Children.Clear();
            matches.Children.Add(Dim($"Couldn't search GitHub: {ex.Message.TrimEnd('.')}."));
            return;
        }

        matches.Children.Clear();
        if (found.Count == 0)
        {
            matches.Children.Add(Dim("Nothing turned up on GitHub."));
            return;
        }
        // Search finds forks for every client, so it's for the user to pick; connecting checks the pick has the addon
        matches.Children.Add(Dim("Found on GitHub. Make sure it's the one for your game version:"));
        foreach (var (repo, description, stars) in found)
        {
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(BodyText(repo));
            content.Children.Add(Dim(description.Length == 0 ? $"★ {stars}" : $"★ {stars} · {description}"));
            var pick = new Button
            {
                Content = content,
                Style = Res<Style>("SecondaryButtonStyle"),
                // The style's fixed height cuts off a description that wraps
                Height = double.NaN,
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(pick, $"Connect to {repo}");
            pick.Click += async (_, _) =>
            {
                flyout.Hide();
                await ConnectAsync(repo, family);
            };
            matches.Children.Add(pick);
        }
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

    private static StackPanel Checking()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        panel.Children.Add(Dim("Checking"));
        return panel;
    }

    private static string RepoName(string repo) => repo[(repo.IndexOf('/') + 1)..];
}
