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

    public GameLauncherService(CredentialService credentialService)
    {
        _credentialService = credentialService;
        _automationService = new PasswordAutomationService();
        _realmlistService = new RealmlistService();
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

            // Wait for the game's login UI to be ready before typing.
            // DirectX/OpenGL games render their login screen after the Win32 window appears,
            // so on a cold start keystrokes fired too early are silently lost.
            if (game.StartupDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(game.StartupDelaySeconds), cancellationToken);

            // Try UI automation to enter password
            var automationResult = await _automationService.AutomatePasswordEntryAsync(
                process.Id,
                password,
                game.InputMethod,
                game.WindowTitle,
                cancellationToken);

            if (automationResult.Success)
            {
                return new LaunchResult(true, "Game launched and password entered automatically.");
            }

            // Fallback: copy to clipboard if automation fails
            await CopyPasswordToClipboardAsync(password);
            return new LaunchResult(true, 
                $"Game launched. Auto-entry failed ({automationResult.Message}). Password copied to clipboard - press Ctrl+V to paste.");
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

    private async Task CopyPasswordToClipboardAsync(string password)
    {
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(password);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

        // Clear clipboard after 30 seconds for security
        await Task.Delay(TimeSpan.FromSeconds(30));
        Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
    }
}

public record LaunchResult(bool Success, string Message);
