using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>Types the password into the game's login screen and presses Enter. Throws <see cref="AppException"/> saying why it couldn't.</summary>
    public async Task AutomatePasswordEntryAsync(
        int processId,
        string password,
        CancellationToken cancellationToken = default)
    {
        // The poll loop makes blocking Win32 calls (SendMessageTimeout can stall up to 1s
        // while the game loads), so keep it off the caller's UI thread.
        var windowHandle = await Task.Run(
            () => WaitForGameWindowAsync(processId, cancellationToken), cancellationToken);

        if (windowHandle == IntPtr.Zero)
            throw new AppException($"No game window appeared within {WindowTimeout.TotalSeconds:0} seconds.");

        await TypePasswordAndSubmitAsync(windowHandle, password);
    }

    private static async Task TypePasswordAndSubmitAsync(IntPtr windowHandle, string password)
    {
        try
        {
            // Run the entire focus + type sequence on a single thread to maintain
            // OS thread affinity required by AttachThreadInput and SendInput.
            await Task.Run(() =>
            {
                if (!ForceForegroundWindow(windowHandle))
                    throw new AppException("Could not bring game window to foreground.");

                // Re-verify focus immediately before typing to prevent password
                // from leaking to a different window that stole focus.
                if (GetForegroundWindow() != windowHandle)
                    throw new AppException("Window lost focus before typing could begin.");

                // Windows can keep a keyboard layout per app, so map characters with the game's, not this app's
                var layout = GetKeyboardLayout(GetWindowThreadProcessId(windowHandle, out _));

                if (!SendKeystrokesForString(password, layout))
                    throw new AppException("SendInput failed to insert all key events. The target window may be running elevated.");

                Thread.Sleep(30);

                // Verify focus is still on the target before pressing Enter
                if (GetForegroundWindow() != windowHandle)
                    throw new AppException("Window lost focus after typing password.");

                if (SendVirtualKey(VK_RETURN, layout) != 2)
                    throw new AppException("Failed to send Enter key.");
            });
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw new AppException($"SendKeys failed: {ex.Message}");
        }
    }

    private static async Task<IntPtr> WaitForGameWindowAsync(int processId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var currentPid = (uint)Environment.ProcessId;
        IntPtr bestWindow = IntPtr.Zero;
        Stopwatch? respondingSince = null;

        // PID → creation time, see ValidateProcessIdentity
        var knownProcesses = new Dictionary<uint, long> { [(uint)processId] = GetProcessCreationTime((uint)processId) };

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

            if (bestWindow == IntPtr.Zero)
            {
                ExpandProcessTree(knownProcesses);

                bestWindow = EnumerateVisibleWindows().FirstOrDefault(hWnd =>
                {
                    GetWindowThreadProcessId(hWnd, out uint windowPid);
                    return windowPid != currentPid
                        && ValidateProcessIdentity(knownProcesses, windowPid)
                        && GetWindowTitle(hWnd).Length > 0;
                });
            }

            // Verify the window is processing messages and wait for a
            // stabilization period so the game's login UI has time to
            // fully render before we start typing.
            if (bestWindow != IntPtr.Zero && IsWindowResponding(bestWindow))
            {
                respondingSince ??= Stopwatch.StartNew();
                if (respondingSince.Elapsed >= StabilizationDelay)
                    return bestWindow;
            }
            else
            {
                respondingSince = null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        // A window that never settled is still a better bet than giving up
        return bestWindow;
    }

    /// <summary>
    /// Adds the launched process's descendants to <paramref name="knownProcesses"/>, since a launcher may start the
    /// real game. Following the tree, rather than matching by name, can't pick up another running copy of the game.
    /// </summary>
    private static void ExpandProcessTree(Dictionary<uint, long> knownProcesses)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return;

        try
        {
            var entry = new PROCESSENTRY32();

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
        // process). Accept it — the PID-tree check still applies.
        if (expectedCreation == 0)
            return true;

        // A current creation time of 0 means OpenProcess failed: the process exited,
        // so a window still reporting this PID belongs to a reused PID.
        if (GetProcessCreationTime(pid) == expectedCreation)
            return true;

        knownProcesses.Remove(pid);
        return false;
    }

    /// <summary>The process's creation time as a FILETIME, or 0 if it can't be opened (exited, or elevated).</summary>
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
    /// characters that have no virtual key mapping on the game's keyboard layout.
    /// </summary>
    private static bool SendKeystrokesForString(string text, IntPtr layout)
    {
        var inputList = new List<INPUT>();

        foreach (char c in text)
        {
            short vkResult = VkKeyScanExW(c, layout);

            if (vkResult == -1)
            {
                inputList.Add(CreateUnicodeKeyInput(c, keyUp: false));
                inputList.Add(CreateUnicodeKeyInput(c, keyUp: true));
                continue;
            }

            ushort vk = (ushort)(vkResult & 0xFF);
            int modifiers = (vkResult >> 8) & 0xFF;

            bool needShift = (modifiers & 1) != 0;
            bool needCtrl = (modifiers & 2) != 0;
            bool needAlt = (modifiers & 4) != 0;

            if (needShift) inputList.Add(CreateVirtualKeyInput(VK_SHIFT, keyUp: false, layout));
            if (needCtrl) inputList.Add(CreateVirtualKeyInput(VK_CONTROL, keyUp: false, layout));
            if (needAlt) inputList.Add(CreateVirtualKeyInput(VK_MENU, keyUp: false, layout));

            inputList.Add(CreateVirtualKeyInput(vk, keyUp: false, layout));
            inputList.Add(CreateVirtualKeyInput(vk, keyUp: true, layout));

            if (needAlt) inputList.Add(CreateVirtualKeyInput(VK_MENU, keyUp: true, layout));
            if (needCtrl) inputList.Add(CreateVirtualKeyInput(VK_CONTROL, keyUp: true, layout));
            if (needShift) inputList.Add(CreateVirtualKeyInput(VK_SHIFT, keyUp: true, layout));
        }

        var inputs = inputList.ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static uint SendVirtualKey(ushort vk, IntPtr layout)
    {
        var inputs = new INPUT[]
        {
            CreateVirtualKeyInput(vk, keyUp: false, layout),
            CreateVirtualKeyInput(vk, keyUp: true, layout)
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

    private static INPUT CreateVirtualKeyInput(ushort vk, bool keyUp, IntPtr layout) => new()
    {
        type = INPUT_KEYBOARD,
        U = { ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = (ushort)MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC, layout),
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
    private static extern short VkKeyScanExW(char ch, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

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
