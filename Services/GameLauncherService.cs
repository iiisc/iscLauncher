using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

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
        var cts = Interlocked.Exchange(ref _clipboardClearCts, null);
        cts?.Cancel();
        cts?.Dispose();
        try { Windows.ApplicationModel.DataTransfer.Clipboard.Clear(); } catch { }
    }

    public async Task<LaunchResult> LaunchGameAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        // Verify executable exists
        if (!System.IO.File.Exists(game.ExecutablePath))
        {
            return new LaunchResult(false, "Executable not found: " + game.ExecutablePath);
        }

        // Update realmlist if configured
        if (!string.IsNullOrWhiteSpace(game.RealmlistAddress))
        {
            var realmlistResult = await _realmlistService.UpdateRealmlistAsync(game.ExecutablePath, game.RealmlistAddress);
            if (!realmlistResult.Success)
            {
                return new LaunchResult(false, realmlistResult.Message);
            }
        }

        // Update config.txt with account name, realm name, and realmlist if configured
        if (!string.IsNullOrWhiteSpace(game.AccountName) || !string.IsNullOrWhiteSpace(game.RealmName) || !string.IsNullOrWhiteSpace(game.RealmlistAddress))
        {
            var configResult = await _realmlistService.UpdateConfigAsync(game.ExecutablePath, game.AccountName, game.RealmName, game.RealmlistAddress);
            if (!configResult.Success)
            {
                return new LaunchResult(false, configResult.Message);
            }
        }

        // Get password from credential manager
        var password = _credentialService.GetCredential(game.CredentialTarget);
        if (string.IsNullOrEmpty(password))
        {
            return new LaunchResult(false, "Password not found in Credential Manager");
        }

        try
        {
            // Start the game process
            var startInfo = new ProcessStartInfo
            {
                FileName = game.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(game.ExecutablePath)
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                return new LaunchResult(false, "Failed to start process");
            }

            // Clipboard mode: copy password and return immediately
            if (game.InputMethod == PasswordInputMethod.Clipboard)
            {
                CopyPasswordToClipboard(password);
                return new LaunchResult(true,
                    "Game launched. Password copied to clipboard - press Ctrl+V to paste.",
                    process.Id);
            }

            // SendKeys mode: wait for the process to finish initialising its
            // message loop before we look for its window and start typing.
            try
            {
                await Task.Run(() => process.WaitForInputIdle(15_000), cancellationToken);
            }
            catch
            {
                // WaitForInputIdle can fail for console apps or certain launch
                // configurations. Continue gracefully — the window-polling loop
                // in PasswordAutomationService will still wait for readiness.
            }

            // Optional extra delay for games that need more time after their
            // message loop is idle (e.g., shader compilation, asset loading).
            if (game.StartupDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(game.StartupDelaySeconds), cancellationToken);

            var automationResult = await _automationService.AutomatePasswordEntryAsync(
                process.Id,
                password,
                game.WindowTitle,
                cancellationToken);

            if (automationResult.Success)
            {
                return new LaunchResult(true, "Game launched and password entered automatically.", process.Id);
            }

            // Fallback: copy to clipboard if SendKeys fails
            CopyPasswordToClipboard(password);
            return new LaunchResult(true,
                $"Game launched. Auto-entry failed ({automationResult.Message}). Password copied to clipboard - press Ctrl+V to paste.",
                process.Id);
        }
        catch (OperationCanceledException)
        {
            return new LaunchResult(false, "Launch operation was cancelled.");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"Failed to launch game: {ex.Message}");
        }
    }

    private void CopyPasswordToClipboard(string password)
    {
        // Cancel and dispose any previous clipboard-clear timer
        var oldCts = Interlocked.Exchange(ref _clipboardClearCts, null);
        oldCts?.Cancel();
        oldCts?.Dispose();

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(password);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

        // Schedule clipboard clear after 30 seconds (cancellable)
        var cts = new CancellationTokenSource();
        _clipboardClearCts = cts;
        _ = ClearClipboardAfterDelayAsync(cts.Token);
    }

    private static async Task ClearClipboardAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
        }
        catch (OperationCanceledException)
        {
            // Timer was cancelled (new copy or app shutdown) — nothing to do
        }
    }
}

public record LaunchResult(bool Success, string Message, int? ProcessId = null);
