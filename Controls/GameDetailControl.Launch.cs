using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Models;
using iscLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Controls;

// Launching, sending the password, and the pull/push offers around a play session
public sealed partial class GameDetailControl
{
    // Games mid-launch or mid-password-entry; both take long enough that a second click would collide
    private readonly HashSet<Guid> _busyGames = new();

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        if (CanSendPassword(game))
            await WhileBusyAsync(game, () => RunLauncherAsync($"Sending password to {game.Name}...",
                () => GameLauncherService!.SendPasswordToRunningGameAsync(game)));
        else
            await LaunchAsync(game);
    }

    /// <summary>
    /// Launches the game. One that's already running is left alone: typing the password into a running game is only
    /// done from the Send Password button, so a stray double-click can't send it into the game's chat.
    /// </summary>
    public async Task LaunchAsync(GameEntry game)
    {
        // A sync may be writing the game's addon files right now
        if (_syncBusy)
        { ShowStatus("Wait for the addon sync to finish before launching.", false); return; }
        var launched = await WhileBusyAsync(game, async () =>
            await OfferPullBeforeLaunchAsync(game)
            && await RunLauncherAsync($"Launching {game.Name}...", () => GameLauncherService!.LaunchGameAsync(game)));
        if (launched)
            await AfterGameClosesAsync(game);
    }

    private async Task<bool> WhileBusyAsync(GameEntry game, Func<Task<bool>> work)
    {
        if (!_busyGames.Add(game.Id))
        { ShowStatus($"Still working on {game.Name}...", false); return false; }
        try
        {
            return await work();
        }
        finally
        {
            _busyGames.Remove(game.Id);
            if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
        }
    }

    private async Task<bool> RunLauncherAsync(string startMessage, Func<Task<LaunchResult>> action)
    {
        ShowInfo(startMessage);
        var result = await action();
        ShowStatus(result.Message, result.Success);
        return result.Success;
    }

    /// <summary>
    /// For a synced game, offers to pull first when another PC has pushed since this one last synced, so you don't play
    /// on old settings and later push them over the newer ones. Returns false if the user cancels the launch.
    /// </summary>
    private async Task<bool> OfferPullBeforeLaunchAsync(GameEntry game)
    {
        if (!game.HasSyncRepo || _syncBusy || GameLauncherService!.IsRunning(game))
            return true;

        GitCommitEntry? change;
        _syncCts?.Cancel(); _syncCts?.Dispose();
        // Being offline shouldn't hold up playing, so the check gives up quickly; Cancel skips it
        _syncCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        SetSyncBusy(true);
        ShowInfo($"Checking {game.Name}'s repo for changes from your other PCs...");
        try
        {
            var session = await AddonSyncService!.OpenAsync(game, null, _syncCts.Token);
            change = await AddonSyncService.FindUnpulledChangeAsync(session, _syncCts.Token);
        }
        // No network, no git or a cancelled check: launch as if there were nothing to pull
        catch (Exception ex)
        {
            Log.Write($"Pull check before launching {game.Name} skipped: {ex.Message}");
            return true;
        }
        finally { SetSyncBusy(false); }

        if (change == null)
            return true;

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Changes From Another PC");
        dialog.Content = $"The repo has a push this PC hasn't pulled: “{change.Message}” ({change.DisplayDate}). Pull it before playing?";
        dialog.PrimaryButtonText = "Pull First";
        dialog.SecondaryButtonText = "Launch Anyway";
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await PullAsync(game);
                return true;
            case ContentDialogResult.Secondary:
                return true;
            default:
                StatusBar.IsOpen = false;
                return false;
        }
    }

    /// <summary>Resets the Launch button when the game closes and, for a synced game, offers to push what changed while playing.</summary>
    private async Task AfterGameClosesAsync(GameEntry game)
    {
        // Battle.net can take a while to start an official game, or wait on an update first
        if (!await GameLauncherService!.WaitForExitAsync(game, TimeSpan.FromMinutes(10)))
            return;
        if (_game?.Id == game.Id) UpdateLaunchButtonState(game);
        if (!game.HasSyncRepo)
            return;

        var reminder = $"{game.Name} closed. Push from the Addons tab to share your changes with your other PCs.";
        if (_syncBusy) { ShowInfo(reminder); return; }

        var dialog = DialogHelper.CreateThemedDialog(XamlRoot, "Push Your Changes?");
        dialog.Content = $"You closed {game.Name}. Push its addons and settings so your other PCs get them?";
        dialog.PrimaryButtonText = "Push";
        dialog.CloseButtonText = "Not Now";
        ContentDialogResult answer;
        // Only one dialog can be open at a time; if another is, the status message is the reminder
        try { answer = await dialog.ShowAsync(); }
        catch (Exception) { ShowInfo(reminder); return; }
        if (answer == ContentDialogResult.Primary)
            await PushAsync(game);
    }

    // Official games log in through Battle.net, so there's never a password to send
    private bool CanSendPassword(GameEntry game) =>
        !BattleNetInstall.IsOfficial(game.ExecutablePath) && GameLauncherService!.IsRunning(game);

    private void UpdateLaunchButtonState(GameEntry game)
    {
        var sendPassword = CanSendPassword(game);
        LaunchButtonText.Text = sendPassword ? "Send Password" : "Launch";
        LaunchButtonIcon.Text = sendPassword ? "🔑" : "⚔";
    }
}
