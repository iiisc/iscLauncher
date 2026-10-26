using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;
using Windows.ApplicationModel.DataTransfer;

namespace iscLauncher.Services;

public class GameLauncherService
{
    private readonly CredentialService _credentialService;
    private readonly PasswordAutomationService _automationService;
    private readonly RealmlistService _realmlistService;
    private CancellationTokenSource? _clipboardClearCts;

    public GameLauncherService(CredentialService credentialService)
    {
        _credentialService = credentialService;
        _automationService = new PasswordAutomationService();
        _realmlistService = new RealmlistService();
    }

    /// <summary>
    /// Call on app shutdown to immediately clear the clipboard if a password is still pending.
    /// </summary>
    public void CancelPendingClipboardClear()
    {
        if (CancelClipboardTimer())
            try { Clipboard.Clear(); } catch { }
    }

    public async Task<LaunchResult> SendPasswordToRunningGameAsync(GameEntry game)
    {
        if (FindRunningProcessId(game) is not int processId)
            return new LaunchResult(false, $"{game.Name} isn't running.");

        var password = _credentialService.GetCredential(game.CredentialTarget);
        if (string.IsNullOrEmpty(password))
            return new LaunchResult(false, "Password not found in Credential Manager");

        var result = await _automationService.AutomatePasswordEntryAsync(processId, password);
        if (result.Success)
            return new LaunchResult(true, "Password sent to running game.");

        CopyPasswordToClipboard(password);
        return new LaunchResult(true,
            $"Auto-entry failed ({result.Message}). Password copied to clipboard - press Ctrl+V to paste.");
    }

    public async Task<LaunchResult> LaunchGameAsync(GameEntry game)
    {
        if (!File.Exists(game.ExecutablePath))
        {
            return new LaunchResult(false, "Executable not found: " + game.ExecutablePath);
        }

        if (BattleNetInstall.Find(game.ExecutablePath) is { } official)
        {
            try
            {
                official.Launch();
                return new LaunchResult(true, $"Starting {game.Name} through Battle.net...");
            }
            catch (Exception ex)
            {
                return new LaunchResult(false, $"Couldn't start Battle.net: {ex.Message}");
            }
        }

        if (IsRunning(game))
            return new LaunchResult(false, $"{game.Name} is already running. Use Send Password to log in.");

        var realmlistResult = await _realmlistService.UpdateRealmlistAsync(game.ExecutablePath, game.RealmlistAddress);
        if (!realmlistResult.Success)
        {
            return new LaunchResult(false, realmlistResult.Message);
        }

        var configResult = await _realmlistService.UpdateConfigAsync(game.ExecutablePath, game.AccountName, game.RealmName, game.RealmlistAddress);
        if (!configResult.Success)
        {
            return new LaunchResult(false, configResult.Message);
        }

        var password = _credentialService.GetCredential(game.CredentialTarget);
        if (string.IsNullOrEmpty(password))
        {
            return new LaunchResult(false, "Password not found in Credential Manager");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = game.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath)
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                return new LaunchResult(false, "Failed to start process");
            }

            // Some games show their window well before the login screen accepts input
            if (game.StartupDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(game.StartupDelaySeconds));

            var automationResult = await _automationService.AutomatePasswordEntryAsync(process.Id, password);
            if (automationResult.Success)
            {
                return new LaunchResult(true, "Game launched and password entered automatically.");
            }

            CopyPasswordToClipboard(password);
            return new LaunchResult(true,
                $"Game launched. Auto-entry failed ({automationResult.Message}). Password copied to clipboard - press Ctrl+V to paste.");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"Failed to launch game: {ex.Message}");
        }
    }

    public bool IsRunning(GameEntry game) => FindRunningProcessId(game) != null;

    /// <summary>
    /// Waits for the game to start and then close. It's looked for by path, since Battle.net starts official games
    /// itself. Returns false if it never started within <paramref name="startTimeout"/>, or can't be watched.
    /// </summary>
    public async Task<bool> WaitForExitAsync(GameEntry game, TimeSpan startTimeout)
    {
        var waiting = Stopwatch.StartNew();
        int? processId;
        while ((processId = FindRunningProcessId(game)) == null)
        {
            if (waiting.Elapsed > startTimeout)
                return false;
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        // Already gone by the time it was opened
        catch (ArgumentException) { }
        // Can't be waited on, e.g. it runs elevated
        catch (Exception) { return false; }
        return true;
    }

    /// <summary>
    /// Finds this game's exe among running processes by path, so it's found however it was started:
    /// by Battle.net, by hand, or before this app was opened.
    /// </summary>
    private static int? FindRunningProcessId(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.ExecutablePath))
            return null;

        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExecutablePath));
        try
        {
            return processes.FirstOrDefault(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, game.ExecutablePath, StringComparison.OrdinalIgnoreCase); }
                // Its path can't be read (e.g. it runs elevated); a process with the same exe name is the best match there is
                catch { return true; }
            })?.Id;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private void CopyPasswordToClipboard(string password)
    {
        CancelClipboardTimer();

        var dataPackage = new DataPackage();
        dataPackage.SetText(password);
        // The timed clear below can't reach Win+V history or other devices, so keep it out of both
        Clipboard.SetContentWithOptions(dataPackage, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });

        var cts = new CancellationTokenSource();
        _clipboardClearCts = cts;
        _ = ClearClipboardAfterDelayAsync(password, cts);
    }

    /// <summary>Stops the pending clear. Returns true if one was pending, meaning our password may still be on the clipboard.</summary>
    private bool CancelClipboardTimer()
    {
        var cts = Interlocked.Exchange(ref _clipboardClearCts, null);
        if (cts == null)
            return false;
        cts.Cancel();
        cts.Dispose();
        return true;
    }

    private async Task ClearClipboardAfterDelayAsync(string password, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _clipboardClearCts, null, cts) != cts)
            return;
        cts.Dispose();

        try
        {
            // Leave the clipboard alone if the user has copied something else since
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) && await content.GetTextAsync() == password)
                Clipboard.Clear();
        }
        catch { }
    }
}

public record LaunchResult(bool Success, string Message);
