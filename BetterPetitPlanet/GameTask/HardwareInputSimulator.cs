using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask;

public static class HardwareInputSimulator
{
    private static ILogger? _logger;

    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    private static readonly Dictionary<string, ushort> ScancodeTable = new(StringComparer.OrdinalIgnoreCase)
    {
        { "A", 0x1E },
        { "B", 0x30 },
        { "C", 0x2E },
        { "D", 0x20 },
        { "E", 0x12 },
        { "F", 0x21 },
        { "G", 0x22 },
        { "H", 0x23 },
        { "I", 0x17 },
        { "J", 0x24 },
        { "K", 0x25 },
        { "L", 0x26 },
        { "M", 0x32 },
        { "N", 0x31 },
        { "O", 0x18 },
        { "P", 0x19 },
        { "Q", 0x10 },
        { "R", 0x13 },
        { "S", 0x1F },
        { "T", 0x14 },
        { "U", 0x16 },
        { "V", 0x2F },
        { "W", 0x11 },
        { "X", 0x2D },
        { "Y", 0x15 },
        { "Z", 0x2C },
        { "SPACE", 0x39 },
        { "空格", 0x39 },
        { "ESC", 0x01 },
        { "ENTER", 0x1C },
        { "回车", 0x1C },
        { "TAB", 0x0F },
        { "1", 0x02 },
        { "2", 0x03 },
        { "3", 0x04 },
        { "4", 0x05 },
        { "5", 0x06 },
        { "6", 0x07 },
        { "7", 0x08 },
        { "8", 0x09 },
        { "9", 0x0A },
        { "0", 0x0B },
        { "F1", 0x3B },
        { "F2", 0x3C },
        { "F3", 0x3D },
        { "F4", 0x3E },
        { "F5", 0x3F },
        { "F6", 0x40 },
        { "F7", 0x41 },
        { "F8", 0x42 },
        { "F9", 0x43 },
        { "F10", 0x44 },
        { "F11", 0x57 },
        { "F12", 0x58 },
    };

    public static ushort GetScancode(string keyName)
    {
        var trimmed = keyName.Trim();
        if (ScancodeTable.TryGetValue(trimmed, out var scancode))
        {
            return scancode;
        }

        if (Enum.TryParse<User32.VK>("VK_" + trimmed.ToUpperInvariant(), true, out var vk))
        {
            return (ushort)(User32.MapVirtualKey((uint)vk, User32.MAPVK.MAPVK_VK_TO_VSC) & 0xFF);
        }

        return 0;
    }

    public static ushort GetVirtualKey(string keyName)
    {
        var trimmed = keyName.Trim();
        if (string.Equals(trimmed, "SPACE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "空格", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_SPACE;
        }
        if (string.Equals(trimmed, "ENTER", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "回车", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_RETURN;
        }
        if (string.Equals(trimmed, "ESC", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_ESCAPE;
        }
        if (string.Equals(trimmed, "TAB", StringComparison.OrdinalIgnoreCase))
        {
            return (ushort)User32.VK.VK_TAB;
        }

        if (Enum.TryParse<User32.VK>("VK_" + trimmed.ToUpperInvariant(), true, out var vk))
        {
            return (ushort)vk;
        }

        if (trimmed.Length == 1)
        {
            char c = char.ToUpperInvariant(trimmed[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                return (ushort)c;
            }
        }

        return 0;
    }

    public static bool SendKeyDown(string keyName)
    {
        var scancode = GetScancode(keyName);
        var vk = GetVirtualKey(keyName);
        if (scancode == 0 && vk == 0) return false;
        return SendKey(scancode, vk, isKeyUp: false);
    }

    public static bool SendKeyUp(string keyName)
    {
        var scancode = GetScancode(keyName);
        var vk = GetVirtualKey(keyName);
        if (scancode == 0 && vk == 0) return false;
        return SendKey(scancode, vk, isKeyUp: true);
    }

    public static bool SendScancode(ushort scancode, bool isKeyUp)
    {
        return SendKey(scancode, 0, isKeyUp);
    }

    public static bool SendKey(ushort scancode, ushort vk, bool isKeyUp)
    {
        var flags = User32.KEYEVENTF.KEYEVENTF_SCANCODE;
        if (isKeyUp)
        {
            flags |= User32.KEYEVENTF.KEYEVENTF_KEYUP;
        }

        var input = new User32.INPUT
        {
            type = User32.INPUTTYPE.INPUT_KEYBOARD,
            ki = new User32.KEYBDINPUT
            {
                wVk = vk,
                wScan = scancode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        };

        var sent = User32.SendInput(1, new[] { input }, Marshal.SizeOf<User32.INPUT>());
        if (sent != 1)
        {
            int err = Marshal.GetLastWin32Error();
            _logger?.LogWarning("SendInput failed for scancode 0x{Scancode:X2} (VK 0x{Vk:X2}), sent={Sent}, win32Err={Err}", scancode, vk, sent, err);
            return false;
        }
        return true;
    }

    public static async Task SendKeyPressAsync(string keyName, int holdDurationMs)
    {
        var scancode = GetScancode(keyName);
        var vk = GetVirtualKey(keyName);
        if (scancode == 0 && vk == 0) return;

        try
        {
            SendKey(scancode, vk, isKeyUp: false);
            await Task.Delay(Math.Clamp(holdDurationMs, 20, 500));
        }
        finally
        {
            SendKey(scancode, vk, isKeyUp: true);
        }
    }

    public static async Task SendBackgroundKeyPressAsync(IntPtr hwnd, string keyName, int holdDurationMs)
    {
        if (hwnd == IntPtr.Zero) return;

        var scancode = GetScancode(keyName);
        uint vkCode = 0;
        if (Enum.TryParse<User32.VK>("VK_" + keyName.Trim().ToUpperInvariant(), true, out var vk))
        {
            vkCode = (uint)vk;
        }
        else
        {
            vkCode = User32.MapVirtualKey(scancode, User32.MAPVK.MAPVK_VSC_TO_VK);
        }

        var targetHwnd = new HWND(hwnd);
        var downLParam = (IntPtr)(1 | (scancode << 16));
        var upLParam = (IntPtr)(1 | (scancode << 16) | (1 << 30) | (1 << 31));

        try
        {
            User32.PostMessage(targetHwnd, User32.WindowMessage.WM_KEYDOWN, (IntPtr)vkCode, downLParam);
            await Task.Delay(Math.Clamp(holdDurationMs, 10, 500));
        }
        finally
        {
            User32.PostMessage(targetHwnd, User32.WindowMessage.WM_KEYUP, (IntPtr)vkCode, upLParam);
        }
    }
}
