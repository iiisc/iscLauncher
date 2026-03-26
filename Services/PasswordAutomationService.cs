using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iscLauncher.Services;

public class PasswordAutomationService
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StabilizationDelay = TimeSpan.FromMilliseconds(1500);
    private const uint ResponsivenessProbeTimeoutMs = 1000;

    public async Task<AutomationResult> AutomatePasswordEntryAsync(
        int processId,
        string password,
        string? windowTitlePattern = null,
        CancellationToken cancellationToken = default)
    {
        var (windowHandle, info) = await WaitForGameWindowAsync(
            processId, windowTitlePattern, cancellationToken);

        if (windowHandle == IntPtr.Zero)
        {
            return new AutomationResult(false, $"No game window found. {info}");
        }

        return await TypePasswordAndSubmitAsync(windowHandle, password);
    }

    private static async Task<AutomationResult> TypePasswordAndSubmitAsync(IntPtr windowHandle, string password)
    {
        try
        {
            // Run the entire focus + type sequence on a single thread to maintain
            // OS thread affinity required by AttachThreadInput and SendInput.
            return await Task.Run(() =>
            {
                if (!ForceForegroundWindow(windowHandle))
                {
                    return new AutomationResult(false, "Could not bring game window to foreground.");
                }

                // Re-verify focus immediately before typing to prevent password
                // from leaking to a different window that stole focus.
                if (GetForegroundWindow() != windowHandle)
                {
                    return new AutomationResult(false, "Window lost focus before typing could begin.");
                }

                if (!SendKeystrokesForString(password))
                {
                    return new AutomationResult(false,
                        "SendInput failed to insert all key events. "
                        + "The target window may be running elevated.");
                }

                Thread.Sleep(30);

                // Verify focus is still on the target before pressing Enter
                if (GetForegroundWindow() != windowHandle)
                {
                    return new AutomationResult(false, "Window lost focus after typing password.");
                }

                if (SendVirtualKey(VK_RETURN) != 2)
                {
                    return new AutomationResult(false, "Failed to send Enter key.");
                }

                return new AutomationResult(true, "Password typed and Enter pressed.");
            });
        }
        catch (Exception ex)
        {
            return new AutomationResult(false, $"SendKeys failed: {ex.Message}");
        }
    }

    private static async Task<(IntPtr handle, string info)> WaitForGameWindowAsync(
        int processId,
        string? windowTitlePattern,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var currentPid = (uint)Environment.ProcessId;
        var checkedWindows = new HashSet<string>();
        var diagnostics = new List<string>();
        IntPtr bestWindow = IntPtr.Zero;
        Stopwatch? respondingSince = null;

        // Track PIDs alongside their creation times so we can detect PID reuse
        // and avoid typing a password into an unrelated process that inherited
        // a recycled PID.
        var knownProcesses = new Dictionary<uint, long>();
        long rootCreation = GetProcessCreationTime((uint)processId);
        knownProcesses[(uint)processId] = rootCreation;

        while (sw.Elapsed < WindowTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If the window we picked earlier is no longer visible (e.g. a
            // splash screen that closed), forget it so we can latch onto the
            // real login window that replaces it.
            if (bestWindow != IntPtr.Zero && !IsWindowVisible(bestWindow))
            {
                bestWindow = IntPtr.Zero;
                respondingSince = null;
            }

            // Walk the process tree rooted at the launched PID to discover
            // child processes that may own the game window (e.g. a launcher
            // that spawns the real game executable). Unlike name+time matching
            // this cannot accidentally target a different instance of the game.
            ExpandProcessTree(knownProcesses);

            foreach (var hWnd in EnumerateVisibleWindows())
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);

                if (!knownProcesses.ContainsKey(windowPid) || windowPid == currentPid)
                    continue;

                // Guard against PID reuse: verify the process behind this window
                // is still the one we originally discovered in the tree.
                if (!ValidateProcessIdentity(knownProcesses, windowPid))
                    continue;

                var title = GetWindowTitle(hWnd);
                if (string.IsNullOrEmpty(title))
                    continue;

                var key = $"{windowPid}:{title}";
                if (checkedWindows.Add(key))
                {
                    diagnostics.Add($"Window: '{title}' (PID {windowPid})");
                }

                if (!string.IsNullOrEmpty(windowTitlePattern)
                    && !title.Contains(windowTitlePattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (bestWindow == IntPtr.Zero)
                    bestWindow = hWnd;
            }

            // Verify the window is processing messages and wait for a
            // stabilization period so the game's login UI has time to
            // fully render before we start typing.
            if (bestWindow != IntPtr.Zero && IsWindowResponding(bestWindow))
            {
                respondingSince ??= Stopwatch.StartNew();
                if (respondingSince.Elapsed >= StabilizationDelay)
                {
                    return (bestWindow, $"Game window found and stable: '{GetWindowTitle(bestWindow)}'");
                }
            }
            else
            {
                respondingSince = null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        if (bestWindow != IntPtr.Zero)
            return (bestWindow, $"Returning window at timeout: '{GetWindowTitle(bestWindow)}'");

        var treeInfo = string.Join(", ", knownProcesses.Keys);
        return (IntPtr.Zero,
            $"Timeout ({WindowTimeout.TotalSeconds}s). Process tree: [{treeInfo}]. "
            + $"Checked {checkedWindows.Count} window(s). "
            + string.Join("; ", diagnostics));
    }

    /// <summary>
    /// Walks the system process list and adds any process whose parent is
    /// already in <paramref name="knownProcesses"/> (i.e. descendants of the
    /// launched process). Records each child's creation time so PID reuse
    /// can be detected later.
    /// </summary>
    private static void ExpandProcessTree(Dictionary<uint, long> knownProcesses)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
                return;

            // Multiple passes handle nested children (launcher → updater → game).
            bool added;
            do
            {
                added = false;
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                if (!Process32First(snapshot, ref entry))
                    break;
                do
                {
                    if (knownProcesses.ContainsKey(entry.th32ParentProcessID)
                        && !knownProcesses.ContainsKey(entry.th32ProcessID))
                    {
                        long creation = GetProcessCreationTime(entry.th32ProcessID);
                        knownProcesses[entry.th32ProcessID] = creation;
                        added = true;
                    }
                } while (Process32Next(snapshot, ref entry));
            } while (added);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// Checks that a tracked PID still refers to the same process we originally
    /// discovered. Returns false (and removes the entry) if the PID has been
    /// reused by an unrelated process — preventing the password from being
    /// typed into the wrong application.
    /// </summary>
    private static bool ValidateProcessIdentity(Dictionary<uint, long> knownProcesses, uint pid)
    {
        if (!knownProcesses.TryGetValue(pid, out long expectedCreation))
            return false;

        // Creation time of 0 means we couldn't query it initially (e.g. elevated
        // process). Accept it — the window-title and PID-tree checks still apply.
        if (expectedCreation == 0)
            return true;

        long currentCreation = GetProcessCreationTime(pid);

        // Process has exited (OpenProcess failed). A dead process can't own
        // windows, so this PID appearing in EnumerateVisibleWindows means
        // it was reused.
        if (currentCreation == 0)
        {
            knownProcesses.Remove(pid);
            return false;
        }

        if (currentCreation != expectedCreation)
        {
            knownProcesses.Remove(pid);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the creation time (as a FILETIME long) for the given PID,
    /// or 0 if the process cannot be opened (e.g. it has already exited
    /// or requires elevated privileges).
    /// </summary>
    private static long GetProcessCreationTime(uint pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
            return 0;

        try
        {
            return GetProcessTimes(hProcess, out long creation, out _, out _, out _)
                ? creation
                : 0;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static bool ForceForegroundWindow(IntPtr targetWindow)
    {
        const int maxAttempts = 3;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (GetForegroundWindow() == targetWindow)
                return true;

            var currentThreadId = GetCurrentThreadId();
            var foregroundThreadId = GetWindowThreadProcessId(GetForegroundWindow(), out _);

            // Attach to the foreground window's thread to bypass SetForegroundWindow restrictions.
            // This must be synchronous so attach/detach happen on the same OS thread.
            bool attached = foregroundThreadId != currentThreadId
                && AttachThreadInput(currentThreadId, foregroundThreadId, true);
            try
            {
                SetForegroundWindow(targetWindow);
                BringWindowToTop(targetWindow);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }

            // Brief wait for the window manager to process the focus change
            Thread.Sleep(50);

            if (GetForegroundWindow() == targetWindow)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Sends each character as virtual key events (WM_KEYDOWN/WM_KEYUP) so that
    /// DirectX/OpenGL games that read input via GetAsyncKeyState, DirectInput, or
    /// Raw Input will actually see the keystrokes. Falls back to Unicode input for
    /// characters that have no virtual key mapping on the current keyboard layout.
    /// </summary>
    private static bool SendKeystrokesForString(string text)
    {
        var inputList = new List<INPUT>();

        foreach (char c in text)
        {
            short vkResult = VkKeyScanW(c);

            if (vkResult == -1)
            {
                // Character has no virtual key mapping; use Unicode fallback
                inputList.Add(CreateUnicodeKeyInput(c, keyUp: false));
                inputList.Add(CreateUnicodeKeyInput(c, keyUp: true));
                continue;
            }

            ushort vk = (ushort)(vkResult & 0xFF);
            int modifiers = (vkResult >> 8) & 0xFF;

            bool needShift = (modifiers & 1) != 0;
            bool needCtrl = (modifiers & 2) != 0;
            bool needAlt = (modifiers & 4) != 0;

            if (needShift) inputList.Add(CreateVirtualKeyInput(VK_SHIFT, keyUp: false));
            if (needCtrl) inputList.Add(CreateVirtualKeyInput(VK_CONTROL, keyUp: false));
            if (needAlt) inputList.Add(CreateVirtualKeyInput(VK_MENU, keyUp: false));

            inputList.Add(CreateVirtualKeyInput(vk, keyUp: false));
            inputList.Add(CreateVirtualKeyInput(vk, keyUp: true));

            if (needAlt) inputList.Add(CreateVirtualKeyInput(VK_MENU, keyUp: true));
            if (needCtrl) inputList.Add(CreateVirtualKeyInput(VK_CONTROL, keyUp: true));
            if (needShift) inputList.Add(CreateVirtualKeyInput(VK_SHIFT, keyUp: true));
        }

        if (inputList.Count == 0)
            return true;

        var inputs = inputList.ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static uint SendVirtualKey(ushort vk)
    {
        var inputs = new INPUT[]
        {
            CreateVirtualKeyInput(vk, keyUp: false),
            CreateVirtualKeyInput(vk, keyUp: true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT CreateUnicodeKeyInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = { ki = new KEYBDINPUT
        {
            wScan = c,
            dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u)
        }}
    };

    private static INPUT CreateVirtualKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = { ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC),
            dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u
        }}
    };

    private static List<IntPtr> EnumerateVisibleWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd))
                windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// Probes whether a window is processing messages by sending a no-op
    /// message with a timeout. Returns false if the window is hung or still
    /// loading (not pumping its message queue).
    /// </summary>
    private static bool IsWindowResponding(IntPtr hWnd)
    {
        var result = SendMessageTimeoutW(
            hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG, ResponsivenessProbeTimeoutMs, out _);
        return result != IntPtr.Zero;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    #region Win32 P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(
        IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime, out long lpUserTime);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint WM_NULL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    private const uint MAPVK_VK_TO_VSC = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    #endregion
}

public record AutomationResult(bool Success, string Message);
