using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using BetterPetitPlanet.Core.Config;
using Vanara.PInvoke;

namespace BetterPetitPlanet.Core.Process;

public sealed class GameProcessDetector
{
    private readonly IConfigService _configService;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

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
            // Ignore desktop attachment failures
        }
    }

    public GameProcessDetector(IConfigService configService)
    {
        _configService = configService;
        EnsureDefaultDesktop();
    }

    public string TargetProcessName => _configService.Config.General.ProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

    public System.Diagnostics.Process? FindProcess()
    {
        var targetName = TargetProcessName;
        var processes = System.Diagnostics.Process.GetProcessesByName(targetName);
        if (processes.Length > 0)
        {
            return processes[0];
        }

        // Secondary check: search processes where title or module contains PetitPlanet
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (proc.ProcessName.Contains("PetitPlanet", StringComparison.OrdinalIgnoreCase) ||
                    proc.MainWindowTitle.Contains("星布谷地", StringComparison.OrdinalIgnoreCase))
                {
                    return proc;
                }
            }
            catch
            {
                // Access denied on some system processes
            }
        }

        return null;
    }

    public IntPtr FindMainWindowHandle()
    {
        EnsureDefaultDesktop();

        // 1. Try EnumWindows first to find visible window with target title or PID
        var targetProcess = FindProcess();
        int targetPid = targetProcess?.Id ?? 0;

        IntPtr bestHwnd = IntPtr.Zero;
        int maxArea = 0;

        User32.EnumWindows((hWnd, lParam) =>
        {
            if (!User32.IsWindowVisible(hWnd))
            {
                return true;
            }

            var textLength = User32.GetWindowTextLength(hWnd);
            var title = string.Empty;
            if (textLength > 0)
            {
                var sb = new StringBuilder(textLength + 1);
                User32.GetWindowText(hWnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            User32.GetWindowThreadProcessId(hWnd, out var pid);

            bool matches = false;
            if (title.Contains("星布谷地", StringComparison.OrdinalIgnoreCase))
            {
                matches = true;
            }
            else if (targetPid > 0 && pid == targetPid)
            {
                matches = true;
            }

            if (matches)
            {
                if (User32.GetClientRect(hWnd, out var rect))
                {
                    int area = rect.Width * rect.Height;
                    if (area > maxArea)
                    {
                        maxArea = area;
                        bestHwnd = (IntPtr)hWnd;
                    }
                }
            }

            return true;
        }, IntPtr.Zero);

        // Fallback: enumerate desktop windows if EnumWindows did not find match
        if (bestHwnd == IntPtr.Zero)
        {
            try
            {
                var hDesk = OpenDesktop("default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    EnumDesktopWindows(hDesk, (hWnd, lParam) =>
                    {
                        if (!User32.IsWindowVisible(new HWND(hWnd))) return true;
                        User32.GetWindowThreadProcessId(new HWND(hWnd), out var pid);
                        if (targetPid > 0 && pid == targetPid)
                        {
                            if (User32.GetClientRect(new HWND(hWnd), out var rect))
                            {
                                int area = rect.Width * rect.Height;
                                if (area > maxArea)
                                {
                                    maxArea = area;
                                    bestHwnd = hWnd;
                                }
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                    CloseDesktop(hDesk);
                }
            }
            catch
            {
                // Ignored
            }
        }

        if (bestHwnd != IntPtr.Zero)
        {
            return bestHwnd;
        }

        // 2. Fallback to process.MainWindowHandle
        if (targetProcess != null && targetProcess.MainWindowHandle != IntPtr.Zero)
        {
            return targetProcess.MainWindowHandle;
        }

        return IntPtr.Zero;
    }

    public bool IsGameRunning()
    {
        return FindMainWindowHandle() != IntPtr.Zero || FindProcess() != null;
    }

    public bool IsGameForeground()
    {
        EnsureDefaultDesktop();
        var hwnd = FindMainWindowHandle();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var fgHwnd = User32.GetForegroundWindow();
        if (fgHwnd == HWND.NULL)
        {
            return false;
        }

        if ((IntPtr)fgHwnd == hwnd)
        {
            return true;
        }

        var rootHwnd = (IntPtr)User32.GetAncestor(fgHwnd, User32.GetAncestorFlag.GA_ROOT);
        if (rootHwnd == hwnd)
        {
            return true;
        }

        User32.GetWindowThreadProcessId(fgHwnd, out var fgPid);
        var targetProc = FindProcess();
        if (targetProc != null && fgPid == targetProc.Id)
        {
            return true;
        }

        return false;
    }

    public bool ActivateGameWindow()
    {
        var hwnd = FindMainWindowHandle();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var targetHwnd = new HWND(hwnd);
        if (User32.IsIconic(targetHwnd))
        {
            User32.ShowWindow(targetHwnd, ShowWindowCommand.SW_RESTORE);
        }

        var fgHwnd = User32.GetForegroundWindow();
        var foregroundThreadId = User32.GetWindowThreadProcessId(fgHwnd, out _);
        var currentThreadId = Kernel32.GetCurrentThreadId();

        if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
        {
            User32.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            User32.SetForegroundWindow(targetHwnd);
            User32.BringWindowToTop(targetHwnd);
            User32.AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
        else
        {
            User32.SetForegroundWindow(targetHwnd);
            User32.BringWindowToTop(targetHwnd);
        }

        return true;
    }

    public Rectangle GetClientSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return Rectangle.Empty;
        }

        if (User32.GetClientRect(new HWND(hwnd), out var rect))
        {
            return new Rectangle(rect.left, rect.top, rect.Width, rect.Height);
        }

        return Rectangle.Empty;
    }
}
