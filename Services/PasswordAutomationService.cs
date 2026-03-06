using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using iscLauncher.Models;

namespace iscLauncher.Services;

public class PasswordAutomationService
{
    private readonly TimeSpan _windowTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public async Task<AutomationResult> AutomatePasswordEntryAsync(
        int processId,
        string password,
        PasswordInputMethod inputMethod,
        string? windowTitlePattern = null,
        CancellationToken cancellationToken = default)
    {
        // If clipboard method, just return immediately - handled by caller
        if (inputMethod == PasswordInputMethod.Clipboard)
        {
            return new AutomationResult(false, "Clipboard method selected - password will be copied to clipboard.");
        }

        using var automation = new UIA3Automation();

        // Get all related process IDs (parent + children)
        var processIds = GetRelatedProcessIds(processId);
        var diagnosticInfo = new List<string>();
        diagnosticInfo.Add($"Input method: {inputMethod}");

        // Wait for ANY window from the game process
        var (window, windowInfo) = await WaitForGameWindowAsync(automation, processIds, windowTitlePattern, diagnosticInfo, cancellationToken);
        if (window == null)
        {
            return new AutomationResult(false, $"No game window found. {string.Join("; ", diagnosticInfo)}");
        }

        diagnosticInfo.Add($"Found window: '{window.Title}'");

        // Use the specified input method
        if (inputMethod == PasswordInputMethod.SendKeys)
        {
            // SendKeys: Focus window and type password directly
            return await TypePasswordWithSendKeysAsync(window, password, diagnosticInfo, cancellationToken);
        }
        else // UIAutomation
        {
            // Try to find a standard password field
            var (passwordBox, fieldInfo) = FindPasswordField(window);

            if (passwordBox != null)
            {
                diagnosticInfo.Add($"Found password field: {fieldInfo}");

                // Enter the password using UI Automation
                try
                {
                    window.Focus();
                    await Task.Delay(100, cancellationToken);

                    passwordBox.Focus();
                    await Task.Delay(100, cancellationToken);

                    if (passwordBox.Patterns.Value.IsSupported)
                    {
                        passwordBox.Patterns.Value.Pattern.SetValue(password);
                    }
                    else
                    {
                        passwordBox.Click();
                        await Task.Delay(50, cancellationToken);

                        FlaUI.Core.Input.Keyboard.TypeSimultaneously(
                            FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                            FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                        await Task.Delay(50, cancellationToken);

                        FlaUI.Core.Input.Keyboard.Type(password);
                    }

                    // Press Enter to submit
                    await Task.Delay(100, cancellationToken);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);

                    return new AutomationResult(true, "Password entered and Enter pressed.");
                }
                catch (Exception ex)
                {
                    return new AutomationResult(false, $"UI Automation failed: {ex.Message}");
                }
            }
            else
            {
                return new AutomationResult(false, $"No password field found. {fieldInfo}. Try using SendKeys method instead.");
            }
        }
    }

    private async Task<AutomationResult> TypePasswordWithSendKeysAsync(
        Window window,
        string password,
        List<string> diagnosticInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var windowHandle = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (windowHandle == IntPtr.Zero)
            {
                return new AutomationResult(false, "Could not get window handle.");
            }

            SetForegroundWindow(windowHandle);
            await Task.Delay(50, cancellationToken); // Brief delay to ensure focus

            if (GetForegroundWindow() != windowHandle)
            {
                return new AutomationResult(false, "Could not bring game window to foreground.");
            }

            // Type the password
            FlaUI.Core.Input.Keyboard.Type(password);

            // Brief delay then press Enter to submit
            await Task.Delay(50, cancellationToken);
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);

            return new AutomationResult(true, "Password typed and Enter pressed.");
        }
        catch (Exception ex)
        {
            return new AutomationResult(false, $"SendKeys failed: {ex.Message}");
        }
    }

    private HashSet<int> GetRelatedProcessIds(int parentProcessId)
    {
        var processIds = new HashSet<int> { parentProcessId };

        try
        {
            // Get the parent process to find its name
            var parentProcess = Process.GetProcessById(parentProcessId);
            var processName = parentProcess.ProcessName;

            // Find all processes with the same name (common for games that restart themselves)
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                processIds.Add(proc.Id);
            }

            // Also get child processes
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    // Check if this process was started around the same time (within 30 seconds)
                    if (proc.StartTime > parentProcess.StartTime.AddSeconds(-5) &&
                        proc.StartTime < DateTime.Now)
                    {
                        // Add recent processes that might be related
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            processIds.Add(proc.Id);
                        }
                    }
                }
                catch
                {
                    // Access denied for some system processes
                }
            }
        }
        catch
        {
            // Process might have exited
        }

        return processIds;
    }

    private async Task<(Window? window, string info)> WaitForGameWindowAsync(
        UIA3Automation automation,
        HashSet<int> processIds,
        string? windowTitlePattern,
        List<string> diagnosticInfo,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var windowsChecked = new HashSet<string>();
        Window? bestWindow = null;

        while (DateTime.UtcNow - startTime < _windowTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var desktop = automation.GetDesktop();
                var windows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));

                foreach (var windowElement in windows)
                {
                    var window = windowElement.AsWindow();
                    if (window == null) continue;

                    int windowProcessId;
                    try
                    {
                        windowProcessId = window.Properties.ProcessId.ValueOrDefault;
                    }
                    catch
                    {
                        continue;
                    }

                    // Check if window belongs to any of our related processes
                    if (!processIds.Contains(windowProcessId))
                        continue;

                    // Skip our own launcher window
                    var title = window.Title ?? string.Empty;
                    if (title.Contains("ISC Game Launcher", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var windowKey = $"{windowProcessId}:{title}";

                    if (!windowsChecked.Contains(windowKey))
                    {
                        windowsChecked.Add(windowKey);
                        diagnosticInfo.Add($"Found window: '{title}' (PID: {windowProcessId})");
                    }

                    // If we have a window title pattern, check it
                    if (!string.IsNullOrEmpty(windowTitlePattern))
                    {
                        if (!title.Contains(windowTitlePattern, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Check if window has a password field - if so, return immediately
                    var (passwordField, _) = FindPasswordField(window);
                    if (passwordField != null)
                    {
                        return (window, $"Found password field in window '{title}'");
                    }

                    // Keep track of the best candidate window (main game window)
                    // Prefer windows with actual content (larger size, visible)
                    if (bestWindow == null && !string.IsNullOrEmpty(title))
                    {
                        bestWindow = window;
                    }
                }

                // If we found a game window (even without password field), return it after a short delay
                // This allows for DirectX/OpenGL games that don't have standard UI controls
                if (bestWindow != null && DateTime.UtcNow - startTime > TimeSpan.FromMilliseconds(500))
                {
                    return (bestWindow, $"Game window found (no standard password field): '{bestWindow.Title}'");
                }
            }
            catch (Exception ex)
            {
                diagnosticInfo.Add($"Enumeration error: {ex.Message}");
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }

        // Return best window if we have one, even at timeout
        if (bestWindow != null)
        {
            return (bestWindow, $"Returning game window at timeout: '{bestWindow.Title}'");
        }

        return (null, $"Timeout after {_windowTimeout.TotalSeconds}s. Windows checked: {windowsChecked.Count}");
    }

    private (AutomationElement? element, string info) FindPasswordField(Window window)
    {
        try
        {
            var editElements = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            var fieldInfo = new List<string>();
            fieldInfo.Add($"Found {editElements.Length} Edit control(s)");

            foreach (var element in editElements)
            {
                try
                {
                    var name = element.Name ?? "(no name)";
                    var automationId = element.AutomationId ?? "(no id)";
                    var isPassword = element.Properties.IsPassword.ValueOrDefault;
                    var className = element.ClassName ?? "(no class)";

                    // Check if it's a password field by checking the IsPassword property
                    if (isPassword)
                    {
                        return (element, $"IsPassword=true, Name='{name}'");
                    }

                    // Some apps use custom password fields - check by name patterns
                    var nameLower = name.ToLowerInvariant();
                    var automationIdLower = automationId.ToLowerInvariant();

                    if (nameLower.Contains("password") || nameLower.Contains("pwd") || nameLower.Contains("pass") ||
                        automationIdLower.Contains("password") || automationIdLower.Contains("pwd") || automationIdLower.Contains("pass"))
                    {
                        return (element, $"Name/ID match, Name='{name}', AutomationId='{automationId}'");
                    }
                }
                catch
                {
                    // Property access can fail
                }
            }

            // Also check for PasswordBox control type (WPF/UWP apps)
            var passwordBoxes = window.FindAllDescendants(cf => cf.ByClassName("PasswordBox"));
            if (passwordBoxes.Length > 0)
            {
                return (passwordBoxes[0], "PasswordBox class found");
            }

            // Last resort: if there's only one or two edit fields, the second one is often password
            if (editElements.Length == 2)
            {
                fieldInfo.Add("Trying second Edit field as password (username/password pattern)");
                return (editElements[1], "Second Edit field (assumed password)");
            }
            if (editElements.Length == 1)
            {
                fieldInfo.Add("Trying only Edit field as password");
                return (editElements[0], "Only Edit field found");
            }

            return (null, string.Join("; ", fieldInfo));
        }
        catch (Exception ex)
        {
            return (null, $"Error: {ex.Message}");
        }
    }
}

public record AutomationResult(bool Success, string Message);
