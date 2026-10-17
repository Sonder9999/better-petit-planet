using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using BetterPetitPlanet.Core.Config;
using Vanara.PInvoke;

namespace BetterPetitPlanet.Core.Process;

public sealed class GameProcessDetector
{
    private static readonly long DiscoveryRefreshIntervalMs = 500;
    private static readonly object ActivationLock = new();

    private readonly IConfigService? _configService;
    private readonly GameWindowDiscoveryCache _discovery;
    private readonly Func<IntPtr> _getForegroundWindow;
    private readonly Func<IntPtr, IntPtr> _getRootWindow;
    private readonly Func<IntPtr, int> _getWindowProcessId;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static void EnsureDefaultDesktop()
    {
        try
        {
            var hDesk = OpenDesktop("default", 0, false, 0x01FF);
            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
                CloseDesktop(hDesk);
            }
        }
        catch
        {
            // Ignore desktop attachment failures.
        }
    }

    public GameProcessDetector(IConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _getForegroundWindow = () => (IntPtr)User32.GetForegroundWindow();
        _getRootWindow = hwnd => (IntPtr)User32.GetAncestor(new HWND(hwnd), User32.GetAncestorFlag.GA_ROOT);
        _getWindowProcessId = hwnd =>
        {
            User32.GetWindowThreadProcessId(new HWND(hwnd), out var pid);
            return unchecked((int)pid);
        };
        _discovery = CreateDiscoveryCache();
        EnsureDefaultDesktop();
    }

    internal GameProcessDetector(
        GameWindowDiscoveryCache cache,
        Func<IntPtr> getForegroundWindow,
        Func<IntPtr, IntPtr> getRootWindow,
        Func<IntPtr, int> getWindowProcessId)
    {
        _discovery = cache ?? throw new ArgumentNullException(nameof(cache));
        _getForegroundWindow = getForegroundWindow ?? throw new ArgumentNullException(nameof(getForegroundWindow));
        _getRootWindow = getRootWindow ?? throw new ArgumentNullException(nameof(getRootWindow));
        _getWindowProcessId = getWindowProcessId ?? throw new ArgumentNullException(nameof(getWindowProcessId));
    }

    public string TargetProcessName => (_configService?.Config.General.ProcessName ?? "PetitPlanet.exe")
        .Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns an owning Process wrapper. Callers are responsible for disposing the returned value.
    /// Internal discovery paths do not use this API and dispose every enumerated wrapper themselves.
    /// </summary>
    public System.Diagnostics.Process? FindProcess()
    {
        var targetName = TargetProcessName;
        var processes = System.Diagnostics.Process.GetProcessesByName(targetName);
        if (processes.Length > 0)
        {
            for (int i = 1; i < processes.Length; i++) processes[i].Dispose();
            return processes[0];
        }

        var allProcesses = System.Diagnostics.Process.GetProcesses();
        System.Diagnostics.Process? match = null;
        try
        {
            foreach (var proc in allProcesses)
            {
                try
                {
                    if (IsGameProcess(proc.ProcessName) ||
                        proc.MainWindowTitle.Contains("星布谷地", StringComparison.OrdinalIgnoreCase))
                    {
                        match = proc;
                        break;
                    }
                }
                catch
                {
                    // Access denied on some system processes.
                }
            }
        }
        finally
        {
            foreach (var proc in allProcesses)
            {
                if (!ReferenceEquals(proc, match)) proc.Dispose();
            }
        }

        return match;
    }

    /// <summary>Explicit/fresh discovery for user initiated actions.</summary>
    public IntPtr FindMainWindowHandle() => _discovery.RefreshSynchronously().MainWindowHandle;

    /// <summary>Non-blocking cached discovery for polling and UI update paths.</summary>
    public IntPtr FindMainWindowHandleCached() => _discovery.GetCurrent().MainWindowHandle;

    public bool IsGameRunning() => _discovery.RefreshSynchronously().IsRunning;

    public bool IsGameRunningCached() => _discovery.GetCurrent().IsRunning;

    public bool IsGameForeground() => IsForegroundForSnapshot(_discovery.RefreshSynchronously());

    public bool IsGameForegroundCached() => IsForegroundForSnapshot(_discovery.GetCurrent());

    internal GameWindowDiscoverySnapshot GetCachedSnapshot() => _discovery.GetCurrent();

    internal bool IsGameForegroundCached(GameWindowDiscoverySnapshot snapshot) => IsForegroundForSnapshot(snapshot);

    private bool IsForegroundForSnapshot(GameWindowDiscoverySnapshot snapshot)
    {
        if (snapshot.MainWindowHandle == IntPtr.Zero) return false;

        var foreground = _getForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == snapshot.MainWindowHandle) return true;

        if (_getRootWindow(foreground) == snapshot.MainWindowHandle) return true;

        // Foreground can change between polling ticks. Return false immediately on a mismatch,
        // while retaining the valid game-window snapshot for the next comparison.
        int foregroundPid = _getWindowProcessId(foreground);
        return snapshot.ProcessId > 0 && foregroundPid == snapshot.ProcessId;
    }

    private GameWindowDiscoveryCache CreateDiscoveryCache() => new(
        discover: DiscoverGameWindow,
        isSnapshotValid: IsSnapshotValid,
        queueWork: work =>
        {
            if (!ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), work))
            {
                throw new InvalidOperationException("Unable to queue game-window discovery.");
            }
        },
        getTimestampMs: () => Environment.TickCount64,
        refreshIntervalMs: DiscoveryRefreshIntervalMs);

    private static bool IsSnapshotValid(GameWindowDiscoverySnapshot snapshot)
    {
        if (!snapshot.IsRunning) return true;

        if (snapshot.MainWindowHandle != IntPtr.Zero)
        {
            var hwnd = new HWND(snapshot.MainWindowHandle);
            if (!User32.IsWindow(hwnd)) return false;
            User32.GetWindowThreadProcessId(hwnd, out var pid);
            return snapshot.ProcessId <= 0 || unchecked((int)pid) == snapshot.ProcessId;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(snapshot.ProcessId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private GameWindowDiscoverySnapshot DiscoverGameWindow()
    {
        try
        {
            EnsureDefaultDesktop();
            var candidate = FindCandidateProcess();
            IntPtr hwnd = candidate.MainWindowHandle;
            int processId = candidate.ProcessId;

            if (processId > 0)
            {
                var processWindow = FindBestWindowForProcess(processId);
                if (processWindow != IntPtr.Zero) hwnd = processWindow;
            }

            if (hwnd == IntPtr.Zero)
            {
                hwnd = FindBestWindowByTitle(out processId);
            }

            return new GameWindowDiscoverySnapshot(processId, hwnd);
        }
        catch
        {
            // Enumeration can race process exit or desktop changes. Return a miss; the cache
            // releases single-flight and retries after its bounded backoff.
            return GameWindowDiscoverySnapshot.Empty;
        }
    }

    private (int ProcessId, IntPtr MainWindowHandle) FindCandidateProcess()
    {
        string targetName = TargetProcessName;
        var namedProcesses = System.Diagnostics.Process.GetProcessesByName(targetName);
        if (namedProcesses.Length > 0)
        {
            int processId = 0;
            IntPtr mainWindow = IntPtr.Zero;
            try
            {
                processId = namedProcesses[0].Id;
                mainWindow = namedProcesses[0].MainWindowHandle;
            }
            catch
            {
                // The process may exit while its metadata is read.
            }
            finally
            {
                foreach (var process in namedProcesses) process.Dispose();
            }
            return (processId, mainWindow);
        }

        var allProcesses = System.Diagnostics.Process.GetProcesses();
        int matchedProcessId = 0;
        IntPtr matchedMainWindow = IntPtr.Zero;
        try
        {
            foreach (var process in allProcesses)
            {
                try
                {
                    if (IsGameProcess(process.ProcessName) ||
                        process.MainWindowTitle.Contains("星布谷地", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedProcessId = process.Id;
                        matchedMainWindow = process.MainWindowHandle;
                        break;
                    }
                }
                catch
                {
                    // Ignore processes whose metadata is inaccessible or which exited mid-read.
                }
            }
        }
        finally
        {
            foreach (var process in allProcesses) process.Dispose();
        }

        return (matchedProcessId, matchedMainWindow);
    }

    private bool IsGameProcess(string processName) =>
        processName.Contains("PetitPlanet", StringComparison.OrdinalIgnoreCase) &&
        !processName.Contains("BetterPetitPlanet", StringComparison.OrdinalIgnoreCase);

    private static IntPtr FindBestWindowForProcess(int processId)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int maxArea = 0;
        User32.EnumWindows((hWnd, _) =>
        {
            if (!User32.IsWindowVisible(hWnd)) return true;
            User32.GetWindowThreadProcessId(hWnd, out var pid);
            if (unchecked((int)pid) == processId)
            {
                ConsiderWindow((IntPtr)hWnd, ref bestWindow, ref maxArea);
            }
            return true;
        }, IntPtr.Zero);

        return bestWindow != IntPtr.Zero ? bestWindow : FindDesktopWindowForProcess(processId);
    }

    private static IntPtr FindDesktopWindowForProcess(int processId)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int maxArea = 0;
        IntPtr desktop = IntPtr.Zero;
        try
        {
            desktop = OpenDesktop("default", 0, false, 0x01FF);
            if (desktop == IntPtr.Zero) return IntPtr.Zero;
            EnumDesktopWindows(desktop, (hWnd, _) =>
            {
                var hwnd = new HWND(hWnd);
                if (!User32.IsWindowVisible(hwnd)) return true;
                User32.GetWindowThreadProcessId(hwnd, out var pid);
                if (unchecked((int)pid) == processId)
                {
                    ConsiderWindow((IntPtr)hWnd, ref bestWindow, ref maxArea);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Fall back to Process.MainWindowHandle.
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
        return bestWindow;
    }

    private static IntPtr FindBestWindowByTitle(out int processId)
    {
        processId = 0;
        int bestProcessId = 0;
        IntPtr bestWindow = IntPtr.Zero;
        int maxArea = 0;
        User32.EnumWindows((hWnd, _) =>
        {
            if (!User32.IsWindowVisible(hWnd)) return true;
            int titleLength = User32.GetWindowTextLength(hWnd);
            if (titleLength <= 0) return true;
            var title = new StringBuilder(titleLength + 1);
            User32.GetWindowText(hWnd, title, title.Capacity);
            if (!title.ToString().Contains("星布谷地", StringComparison.OrdinalIgnoreCase)) return true;

            if (ConsiderWindow((IntPtr)hWnd, ref bestWindow, ref maxArea))
            {
                User32.GetWindowThreadProcessId(hWnd, out var pid);
                bestProcessId = unchecked((int)pid);
            }
            return true;
        }, IntPtr.Zero);

        if (bestWindow != IntPtr.Zero)
        {
            processId = bestProcessId;
            return bestWindow;
        }

        IntPtr desktop = IntPtr.Zero;
        try
        {
            desktop = OpenDesktop("default", 0, false, 0x01FF);
            if (desktop == IntPtr.Zero) return IntPtr.Zero;
            EnumDesktopWindows(desktop, (hWnd, _) =>
            {
                var hwnd = new HWND(hWnd);
                if (!User32.IsWindowVisible(hwnd)) return true;
                int titleLength = User32.GetWindowTextLength(hwnd);
                if (titleLength <= 0) return true;
                var title = new StringBuilder(titleLength + 1);
                User32.GetWindowText(hwnd, title, title.Capacity);
                if (!title.ToString().Contains("星布谷地", StringComparison.OrdinalIgnoreCase)) return true;

                if (ConsiderWindow((IntPtr)hWnd, ref bestWindow, ref maxArea))
                {
                    User32.GetWindowThreadProcessId(hwnd, out var pid);
                    bestProcessId = unchecked((int)pid);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // The regular window enumeration remains authoritative when the desktop cannot open.
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }

        processId = bestProcessId;
        return bestWindow;
    }

    private static bool ConsiderWindow(IntPtr hWnd, ref IntPtr bestWindow, ref int maxArea)
    {
        if (!User32.GetClientRect(new HWND(hWnd), out var rect)) return false;
        int area = rect.Width * rect.Height;
        if (area <= maxArea) return false;
        maxArea = area;
        bestWindow = hWnd;
        return true;
    }

    public bool ActivateGameWindow()
    {
        lock (ActivationLock)
        {
            var snapshot = _discovery.RefreshSynchronously();
            var hwnd = snapshot.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return false;

            var targetHwnd = new HWND(hwnd);

            // 1. 若当前前台窗口已经是目标游戏窗口，直接返回成功，杜绝任何多余的按键模拟与窗口操作
            var fgHwnd = (IntPtr)User32.GetForegroundWindow();
            if (fgHwnd == hwnd) return true;

            // 2. 检查最小化状态，若已最小化则恢复
            if (User32.IsIconic(targetHwnd))
            {
                User32.ShowWindow(targetHwnd, ShowWindowCommand.SW_RESTORE);
            }
            else
            {
                User32.ShowWindow(targetHwnd, ShowWindowCommand.SW_SHOW);
            }

            SwitchToThisWindow(hwnd, true);

            if ((IntPtr)User32.GetForegroundWindow() == hwnd) return true;

            // 3. 尝试标准前台激活 API
            User32.BringWindowToTop(targetHwnd);
            if (User32.SetForegroundWindow(targetHwnd))
            {
                if ((IntPtr)User32.GetForegroundWindow() == hwnd) return true;
            }

            // 4. 若标准前台激活受限于 Windows UIPI / SetForegroundWindow 规则，采用 AttachThreadInput + Alt 模拟兜底
            var foregroundHwnd = User32.GetForegroundWindow();
            var foregroundThreadId = User32.GetWindowThreadProcessId(foregroundHwnd, out _);
            var targetThreadId = User32.GetWindowThreadProcessId(targetHwnd, out _);
            var currentThreadId = Kernel32.GetCurrentThreadId();

            bool attachedForeground = false;
            bool attachedTarget = false;

            try
            {
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attachedForeground = User32.AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }
                if (targetThreadId != 0 && targetThreadId != currentThreadId)
                {
                    attachedTarget = User32.AttachThreadInput(currentThreadId, targetThreadId, true);
                }

                // 检查物理按键状态：
                // 如果用户本人正在物理按住 Alt，严禁发送任何合成的 Alt 按键或释放，避免打断破坏用户的物理输入；
                // 如果用户未物理按住 Alt，则通过 try...finally 严格保障成对发送 Alt Down 与 Alt Up。
                bool wasAltPhysicallyDown = (User32.GetAsyncKeyState((ushort)User32.VK.VK_MENU) & 0x8000) != 0;
                bool simulatedAltDown = false;

                try
                {
                    if (!wasAltPhysicallyDown)
                    {
                        User32.keybd_event(0x12, 0, 0, IntPtr.Zero);
                        simulatedAltDown = true;
                    }

                    User32.SetForegroundWindow(targetHwnd);
                    User32.BringWindowToTop(targetHwnd);
                }
                finally
                {
                    if (simulatedAltDown)
                    {
                        User32.keybd_event(0x12, 0, User32.KEYEVENTF.KEYEVENTF_KEYUP, IntPtr.Zero);
                    }
                    else if (!wasAltPhysicallyDown)
                    {
                        // 补充防御：如果用户并没有物理按住 Alt，但检测到 Alt 处于卡键状态（例如之前的残留泄漏）
                        // 则发送一次 Alt Up 确保消除卡键
                        if ((User32.GetAsyncKeyState((ushort)User32.VK.VK_MENU) & 0x8000) != 0)
                        {
                            User32.keybd_event(0x12, 0, User32.KEYEVENTF.KEYEVENTF_KEYUP, IntPtr.Zero);
                        }
                    }
                }
            }
            finally
            {
                // 一对一安全解绑 AttachThreadInput，杜绝任何输入队列或线程锁泄露
                if (attachedTarget)
                {
                    try { User32.AttachThreadInput(currentThreadId, targetThreadId, false); } catch { }
                }
                if (attachedForeground)
                {
                    try { User32.AttachThreadInput(currentThreadId, foregroundThreadId, false); } catch { }
                }
            }

            return IsForegroundForSnapshot(snapshot);
        }
    }

    public Rectangle GetClientSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Rectangle.Empty;

        if (User32.GetClientRect(new HWND(hwnd), out var rect))
            return new Rectangle(rect.left, rect.top, rect.Width, rect.Height);

        return Rectangle.Empty;
    }
}
