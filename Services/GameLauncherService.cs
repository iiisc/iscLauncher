using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    // Set while a copied password is waiting to be cleared from the clipboard
    private CancellationTokenSource? _clipboardClear;

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
        if (_clipboardClear == null)
            return;
        _clipboardClear.Cancel();
        _clipboardClear = null;
        try { Clipboard.Clear(); } catch { }
    }

    /// <summary>Types the password into a running game. Returns what happened; throws <see cref="AppException"/> if it couldn't start.</summary>
    public async Task<string> SendPasswordToRunningGameAsync(GameEntry game)
    {
        if (FindRunningProcessId(game) is not int processId)
            throw new AppException($"{game.Name} isn't running.");

        var password = PasswordFor(game);
        using var process = Process.GetProcessById(processId);
        return await TypeOrCopyPasswordAsync(process, password, "Password sent to running game.", "");
    }

    /// <summary>
    /// An official install with no saved password. Started directly, an official game shows its own login screen, so
    /// one with a password is launched and logged in like any other.
    /// </summary>
    public bool OpensInBattleNet(GameEntry game) =>
        BattleNetInstall.IsOfficial(game.ExecutablePath) && string.IsNullOrEmpty(_credentialService.GetCredential(game.CredentialTarget));

    /// <summary>
    /// Starts the game and logs in, or opens it in Battle.net (see <see cref="OpensInBattleNet"/>). Returns what
    /// happened; throws <see cref="AppException"/> if it couldn't.
    /// </summary>
    public async Task<string> LaunchGameAsync(GameEntry game)
    {
        if (!File.Exists(game.ExecutablePath))
            throw new AppException("Executable not found: " + game.ExecutablePath);

        if (OpensInBattleNet(game) && BattleNetInstall.Find(game.ExecutablePath) is { } official)
        {
            try
            {
                official.Open();
                return $"Battle.net is open on {game.Name}. Press Play there to start it.";
            }
            catch (Exception ex)
            {
                throw new AppException($"Couldn't open Battle.net: {ex.Message}");
            }
        }

        if (IsRunning(game))
            throw new AppException($"{game.Name} is already running. Use Send Password to log in.");

        // An official client reads neither, and remembers the account's email itself
        if (!BattleNetInstall.IsOfficial(game.ExecutablePath))
            await _realmlistService.ApplyAsync(game);
        var password = _credentialService.GetCredential(game.CredentialTarget);

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = game.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = game.GameDir
            }) ?? throw new AppException("Failed to start process");
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw new AppException($"Failed to launch game: {ex.Message}");
        }

        using (process)
        {
            if (string.IsNullOrEmpty(password))
                return "Game launched. No password is saved, so log in yourself.";

            // Some games show their window well before the login screen accepts input
            if (game.StartupDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(game.StartupDelaySeconds));

            return await TypeOrCopyPasswordAsync(process, password, "Game launched and password entered automatically.", "Game launched. ");
        }
    }

    private string PasswordFor(GameEntry game) =>
        _credentialService.GetCredential(game.CredentialTarget) is { Length: > 0 } password
            ? password
            : throw new AppException("Password not found in Credential Manager");

    // Typing can fail for reasons outside the app's control; the clipboard is the fallback, so that still counts as success
    private async Task<string> TypeOrCopyPasswordAsync(Process game, string password, string typedMessage, string prefix)
    {
        try
        {
            await _automationService.AutomatePasswordEntryAsync(game, password);
            return typedMessage;
        }
        catch (AppException ex)
        {
            CopyPasswordToClipboard(password);
            return $"{prefix}Auto-entry failed ({ex.Message}). Password copied to clipboard - press Ctrl+V to paste.";
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
            // A process whose path can't be read at all is still the best match there is
            return processes.FirstOrDefault(p => ImagePath(p.Id) is not { } path
                || string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// The process's exe, or null if it can't be opened. Process.MainModule can't read it from a game running as
    /// administrator, which made any elevated copy of the exe count as every game using that exe name.
    /// </summary>
    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // Granted even for elevated processes, unlike the access Process.MainModule needs
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // Like everything that copies the password, these run on the UI thread, which the clipboard needs
    private void CopyPasswordToClipboard(string password)
    {
        _clipboardClear?.Cancel();

        var dataPackage = new DataPackage();
        dataPackage.SetText(password);
        // The timed clear below can't reach Win+V history or other devices, so keep it out of both
        Clipboard.SetContentWithOptions(dataPackage, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });

        _clipboardClear = new CancellationTokenSource();
        _ = ClearClipboardAfterDelayAsync(password, _clipboardClear.Token);
    }

    private async Task ClearClipboardAfterDelayAsync(string password, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
        // A newer copy or the app closing took over
        catch (OperationCanceledException)
        {
            return;
        }
        _clipboardClear = null;

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
